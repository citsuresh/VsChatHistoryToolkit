using System.Runtime.InteropServices;
using System.Text.Json;

namespace VsChatHistoryToolkit;

internal static class EventsSummaryScanner
{

    public static EventsResult Scan(string sessionId, string path)
    {
        try { return ScanCore(sessionId, path); }
        catch (IOException) { throw CannotReadEvents(); }
        catch (UnauthorizedAccessException) { throw CannotReadEvents(); }
    }

    private static EventsResult ScanCore(string sessionId, string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return Missing(sessionId);
        }
        catch (DirectoryNotFoundException)
        {
            return Missing(sessionId);
        }

        using (stream)
        {
            var builder = new SummaryBuilder();
            var buffer = new byte[8192];
            var line = new List<byte>(8192);
            var lineTooLong = false;
            var lineHasBytes = false;
            var lastWasCr = false;
            var bytesScanned = 0L;
            var linesScanned = 0;
            var complete = true;
            var length = stream.Length;

            void FinishLine(bool parse)
            {
                linesScanned++;
                if (lineTooLong)
                {
                    complete = false;
                }
                else if (parse)
                {
                    builder.AddLine(CollectionsMarshal.AsSpan(line));
                }
                line.Clear();
                lineTooLong = false;
                lineHasBytes = false;
            }

            while (bytesScanned < Caps.EventsMaxBytes)
            {
                var wanted = (int)Math.Min(buffer.Length, Caps.EventsMaxBytes - bytesScanned);
                var read = stream.Read(buffer, 0, wanted);
                if (read == 0) break;

                var stop = false;
                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];
                    if (linesScanned >= Caps.EventsMaxLines)
                    {
                        if (lastWasCr && b == (byte)'\n')
                        {
                            bytesScanned++;
                            lastWasCr = false;
                            continue;
                        }
                        complete = false;
                        stop = true;
                        break;
                    }

                    bytesScanned++;
                    if (b == (byte)'\n' && lastWasCr)
                    {
                        lastWasCr = false;
                        continue;
                    }
                    if (b == (byte)'\r')
                    {
                        FinishLine(parse: true);
                        lastWasCr = true;
                        continue;
                    }
                    if (b == (byte)'\n')
                    {
                        lastWasCr = false;
                        FinishLine(parse: true);
                        continue;
                    }

                    lastWasCr = false;
                    lineHasBytes = true;
                    if (lineTooLong) continue;
                    if (line.Count == Caps.EventsMaxLineBytes)
                    {
                        lineTooLong = true;
                        line.Clear();
                        continue;
                    }
                    line.Add(b);
                }

                if (stop) break;
                if (read < wanted) break;
            }

            if (bytesScanned >= Caps.EventsMaxBytes && bytesScanned < length)
            {
                complete = false;
                if (lineHasBytes) FinishLine(parse: false);
            }
            else if (bytesScanned >= length && lineHasBytes && linesScanned < Caps.EventsMaxLines)
            {
                FinishLine(parse: !lineTooLong);
            }
            else if (lineTooLong)
            {
                complete = false;
            }

            if (bytesScanned < length) complete = false;
            return builder.ToResult(sessionId, complete, bytesScanned, linesScanned);
        }
    }

    private static ToolkitException CannotReadEvents() =>
        new(ErrorKind.StoreUnavailable, "Cannot read events.jsonl.");

    private static EventsResult Missing(string sessionId) =>
        new(sessionId, false, "No events.jsonl is recorded for this session.", new EventScan(true, 0, 0),
            Array.Empty<EventCountItem>(), Array.Empty<EventNameCount>(), Array.Empty<EventNameCount>(), Array.Empty<EventNameCount>(),
            0, 0, 0, Array.Empty<EventCompaction>(), Array.Empty<EventShutdown>());

    private sealed class SummaryBuilder
    {
        private readonly Dictionary<string, long> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _tools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _permissions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _skills = new(StringComparer.Ordinal);
        private readonly List<EventCompaction> _compactions = new();
        private readonly List<EventShutdown> _shutdowns = new();
        private long _aborts;
        private long _subagentsStarted;
        private long _compactionCount;
        private bool _truncated;

        public void AddLine(ReadOnlySpan<byte> line)
        {
            if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF) line = line[3..];
            var type = ReadType(line);
            if (type is null) return;
            Increment(_types, type);
            if (type is not ("abort" or "subagent.started" or "tool.execution_start" or "permission.requested" or "skill.invoked" or "session.compaction_complete" or "session.shutdown"))
                return;

            try
            {
                using var doc = JsonDocument.Parse(line.ToArray());
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var data = Property(root, "data");
                switch (type)
                {
                    case "abort":
                        _aborts++;
                        break;
                    case "subagent.started":
                        _subagentsStarted++;
                        break;
                    case "tool.execution_start":
                        IncrementIfValue(_tools, FirstString(Property(data, "toolName"), Property(data, "name")));
                        break;
                    case "permission.requested":
                        var permission = Property(data, "permissionRequest");
                        var request = Property(data, "request");
                        IncrementIfValue(_permissions, FirstString(Property(permission, "kind"), Property(request, "kind")));
                        break;
                    case "skill.invoked":
                        IncrementIfValue(_skills, String(Property(data, "name")));
                        break;
                    case "session.compaction_complete":
                        _compactionCount++;
                        if (_compactions.Count < Caps.EventsCompactions) _compactions.Add(ReadCompaction(data));
                        else _truncated = true;
                        break;
                    case "session.shutdown":
                        if (_shutdowns.Count < Caps.EventsShutdowns) _shutdowns.Add(ReadShutdown(data));
                        else _truncated = true;
                        break;
                }
            }
            catch (JsonException)
            {
                // The event type was readable, but malformed or partial data fields are omitted.
            }
        }

        public EventsResult ToResult(string sessionId, bool complete, long bytes, int lines)
        {
            var truncated = !complete || _truncated;
            var eventCounts = Top(_types, ref truncated).Select(x => new EventCountItem(x.Name, x.Count)).ToArray();
            var tools = Top(_tools, ref truncated);
            var permissions = Top(_permissions, ref truncated);
            var skills = Top(_skills, ref truncated);
            return new EventsResult(sessionId, true, null, new EventScan(complete, bytes, lines), eventCounts, tools, permissions,
                skills, _aborts, _subagentsStarted, _compactionCount, _compactions, _shutdowns) { Truncated = truncated };
        }

        private EventShutdown ReadShutdown(JsonElement data)
        {
            var changes = Property(data, "codeChanges");
            var modelItems = new List<EventModelMetric>();
            var metrics = Property(data, "modelMetrics");
            if (metrics.ValueKind == JsonValueKind.Array)
            {
                foreach (var metric in metrics.EnumerateArray())
                {
                    if (modelItems.Count == Caps.EventsTop) { _truncated = true; break; }
                    var requests = Property(metric, "requests");
                    var requestDetails = requests.ValueKind == JsonValueKind.Object
                        ? new EventModelRequests(Integer(Property(requests, "count")), Decimal(Property(requests, "cost")))
                        : null;
                    modelItems.Add(new EventModelMetric(CapText(String(Property(metric, "model"))), requestDetails));
                }
            }

            return new EventShutdown(
                CapText(String(Property(data, "shutdownType"))), Integer(Property(data, "totalPremiumRequests")), Integer(Property(data, "totalNanoAiu")),
                Integer(Property(changes, "linesAdded")), Integer(Property(changes, "linesRemoved")), Count(Property(changes, "filesModified")),
                Integer(Property(data, "currentTokens")), Integer(Property(data, "systemTokens")), Integer(Property(data, "conversationTokens")),
                Integer(Property(data, "toolDefinitionsTokens")), Integer(Property(data, "totalApiDurationMs")),
                metrics.ValueKind == JsonValueKind.Array ? modelItems : null);
        }

        private EventCompaction ReadCompaction(JsonElement data) => new(
            Integer(Property(data, "checkpointNumber")), CapText(String(Property(data, "trigger"))), Boolean(Property(data, "success")),
            Integer(Property(data, "preCompactionTokens")), Integer(Property(data, "postCompactionTokens")), Integer(Property(data, "tokensRemoved")),
            Integer(Property(data, "messagesRemoved")), Integer(Property(data, "tokenLimit")));

        private string? CapText(string? value)
        {
            if (value is null) return null;
            var capped = TextUtil.Cap(value, Caps.TitleChars, out var cut);
            _truncated |= cut;
            return capped;
        }

        private static IReadOnlyList<EventNameCount> Top(Dictionary<string, long> values, ref bool truncated)
        {
            if (values.Count > Caps.EventsTop) truncated = true;
            var items = new List<EventNameCount>();
            foreach (var item in values.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Caps.EventsTop))
            {
                var name = TextUtil.Cap(item.Key, Caps.TitleChars, out var cut);
                truncated |= cut;
                items.Add(new EventNameCount(name, item.Value));
            }
            return items;
        }

        private static string? ReadType(ReadOnlySpan<byte> line)
        {
            try
            {
                var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) return null;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var isType = reader.CurrentDepth == 1 && reader.ValueTextEquals("type"u8);
                    if (!reader.Read()) return null;
                    if (isType) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    reader.Skip();
                }
            }
            catch (JsonException) { }
            return null;
        }

        private static void IncrementIfValue(Dictionary<string, long> counts, string? value)
        {
            if (!string.IsNullOrEmpty(value)) Increment(counts, value);
        }

        private static void Increment(Dictionary<string, long> counts, string value) =>
            counts[value] = counts.GetValueOrDefault(value) + 1;

        private static JsonElement Property(JsonElement value, string name) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;

        private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static string? FirstString(JsonElement first, JsonElement second)
        {
            var value = String(first);
            return string.IsNullOrEmpty(value) ? String(second) : value;
        }

        private static long? Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

        private static decimal? Decimal(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) ? n : null;

        private static bool? Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

        private static int? Count(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength(),
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            _ => null,
        };
    }
}
