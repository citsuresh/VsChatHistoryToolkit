using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class DoctorTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";
    private const string D = "44444444-4444-4444-4444-444444444444";

    /// <summary>A: row, folder, turn, usage. B: row and folder, no turns, no usage. C: row only. D: folder only.</summary>
    private static FixtureStore MixedStore()
    {
        var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddIdleSession(A);
        fx.AddUsage(A, 0, "m", 1, 1);
        fx.AddSession(B);
        fx.WriteYaml(B, FixtureStore.SampleYaml(B));
        fx.AddSession(C);
        fx.WriteEventsRaw(D, new string('x', 300));
        fx.WriteEventsRaw(A, new string('x', 100));
        return fx;
    }

    [Fact]
    public void Doctor_counts_rows_folders_and_mismatches()
    {
        using var fx = MixedStore();

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.Equal(3, d.SessionRows);
        Assert.True(d.SessionStateFolderPresent);
        Assert.Equal(3, d.SessionFolders);
        Assert.Equal(1, d.RowsWithoutFolder);
        Assert.Equal(new[] { C }, d.RowsWithoutFolderIds);
        Assert.Equal(1, d.FoldersWithoutRow);
        Assert.Equal(new[] { D }, d.FoldersWithoutRowNames);
        Assert.Equal(2, d.SessionsWithoutTurns);
        Assert.Equal(2, d.SessionsWithoutUsage);
        Assert.False(d.Truncated);
        Assert.Equal(Caps.Coverage, d.Coverage);
    }

    [Fact]
    public void Doctor_lists_the_largest_events_files_with_ids_and_sizes_only()
    {
        using var fx = MixedStore();

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.Equal(new[] { new EventsFileItem(D, 300), new EventsFileItem(A, 100) }, d.LargestEventsFiles);
        var json = JsonOut.Serialize(d);
        Assert.DoesNotContain("xxx", json);
        Assert.DoesNotContain(fx.Root.Replace("\\", "\\\\"), json);
    }

    [Fact]
    public void Doctor_keeps_the_ten_largest_events_files_ties_by_id_and_flags_the_cut()
    {
        using var fx = new FixtureStore();
        for (var i = 0; i < Caps.DoctorLargestFiles + 2; i++)
            fx.WriteEventsRaw($"s{i:D2}", new string('x', i < 4 ? 50 : 10 + i));

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.Equal(Caps.DoctorLargestFiles, d.LargestEventsFiles.Count);
        Assert.Equal(new[] { "s00", "s01", "s02", "s03", "s11", "s10" }, d.LargestEventsFiles.Take(6).Select(e => e.SessionId));
        Assert.Equal(50, d.LargestEventsFiles[0].Bytes);
        Assert.DoesNotContain(d.LargestEventsFiles, e => e.SessionId is "s04" or "s05");
        Assert.True(d.Truncated);
    }

    [Fact]
    public void Doctor_caps_the_id_lists_like_stats_but_keeps_full_counts()
    {
        using var fx = new FixtureStore();
        for (var i = 0; i < Caps.StatsTop + 5; i++)
        {
            fx.AddSession($"row-{i:D3}");
            Directory.CreateDirectory(fx.SessionDir($"dir-{i:D3}"));
        }

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.Equal(Caps.StatsTop + 5, d.RowsWithoutFolder);
        Assert.Equal(Caps.StatsTop, d.RowsWithoutFolderIds.Count);
        Assert.Equal("row-000", d.RowsWithoutFolderIds[0]);
        Assert.Equal(Caps.StatsTop + 5, d.FoldersWithoutRow);
        Assert.Equal(Caps.StatsTop, d.FoldersWithoutRowNames.Count);
        Assert.Equal("dir-000", d.FoldersWithoutRowNames[0]);
        Assert.True(d.Truncated);
    }

    [Fact]
    public void Doctor_omits_usage_counts_without_the_usage_table()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.Null(d.SessionsWithoutUsage);
        Assert.DoesNotContain("sessions_without_usage", JsonOut.Serialize(d));
        Assert.Equal(0, d.SessionsWithoutTurns);
    }

    [Fact]
    public void Doctor_omits_usage_counts_when_the_usage_table_lacks_a_column()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable(dropColumn: true);
        fx.AddSession(A);

        Assert.Null(new ChatStore(fx.Paths).Doctor().SessionsWithoutUsage);
    }

    [Fact]
    public void Doctor_reports_unknown_folder_counts_when_session_state_cannot_be_listed()
    {
        using var fx = MixedStore();
        var store = new ChatStore(fx.Paths) { ListDirectories = _ => throw new UnauthorizedAccessException("denied") };

        var d = store.Doctor();

        Assert.True(d.SessionStateFolderPresent);
        Assert.False(d.SessionStateListed);
        Assert.Null(d.SessionFolders);
        Assert.Null(d.RowsWithoutFolder);
        Assert.Null(d.FoldersWithoutRow);
        Assert.Empty(d.RowsWithoutFolderIds);
        Assert.Empty(d.FoldersWithoutRowNames);
        Assert.Empty(d.LargestEventsFiles);
        Assert.Equal(3, d.SessionRows);
        Assert.Equal(2, d.SessionsWithoutTurns);
        Assert.True(d.Truncated);

        var json = JsonOut.Serialize(d);
        Assert.Contains("\"session_state_listed\":false", json);
        Assert.DoesNotContain("\"session_folders\"", json);
        Assert.DoesNotContain("\"rows_without_folder\"", json);
        Assert.DoesNotContain("\"folders_without_row\"", json);
    }

    [Fact]
    public void Doctor_discards_a_listing_that_fails_partway()
    {
        using var fx = MixedStore();
        static IEnumerable<string> Partial(string dir)
        {
            yield return Path.Combine(dir, A);
            throw new IOException("gone");
        }
        var store = new ChatStore(fx.Paths) { ListDirectories = Partial };

        var d = store.Doctor();

        Assert.False(d.SessionStateListed);
        Assert.Null(d.SessionFolders);
        Assert.Null(d.RowsWithoutFolder);
        Assert.Empty(d.LargestEventsFiles);
        Assert.True(d.Truncated);
    }

    [Fact]
    public void Doctor_skips_an_events_file_whose_size_cannot_be_read()
    {
        using var fx = MixedStore();
        var real = new ChatStore(fx.Paths).FileLength;
        var store = new ChatStore(fx.Paths)
        {
            FileLength = p => p.Contains(D, StringComparison.Ordinal) ? throw new IOException("locked") : real(p),
        };

        var d = store.Doctor();

        Assert.Equal(new[] { new EventsFileItem(A, 100) }, d.LargestEventsFiles);
        Assert.True(d.SessionStateListed);
        Assert.Equal(3, d.SessionFolders);
        Assert.Equal(1, d.FoldersWithoutRow);
        Assert.True(d.Truncated);
    }

    [Fact]
    public void Doctor_listing_flag_is_true_when_session_state_is_listed()
    {
        using var fx = MixedStore();
        Assert.True(new ChatStore(fx.Paths).Doctor().SessionStateListed);
    }

    [Fact]
    public void Cli_doctor_text_says_when_folder_counts_are_unknown()
    {
        using var fx = MixedStore();
        var d = new ChatStore(fx.Paths) { ListDirectories = _ => throw new IOException("denied") }.Doctor();

        var output = TextFormatter.Format(d);

        Assert.Contains("session-state could not be listed: folder counts are unknown", output);
        Assert.Contains("session folders: -", output);
        Assert.Contains("rows without a folder: -", output);
        Assert.Contains("folders without a row: -", output);
        Assert.Contains("(output truncated)", output);
    }

    [Fact]
    public void Doctor_reports_a_missing_session_state_folder()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        Directory.Delete(fx.SessionStateDir, recursive: true);

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.False(d.SessionStateFolderPresent);
        Assert.True(d.SessionStateListed);
        Assert.Equal(0, d.SessionFolders);
        Assert.Equal(1, d.RowsWithoutFolder);
        Assert.Empty(d.LargestEventsFiles);
    }

    [Fact]
    public void Doctor_on_an_empty_store_reports_zeros()
    {
        using var fx = new FixtureStore();

        var d = new ChatStore(fx.Paths).Doctor();

        Assert.Equal(0, d.SessionRows);
        Assert.Equal(0, d.SessionFolders);
        Assert.Empty(d.RowsWithoutFolderIds);
        Assert.Empty(d.FoldersWithoutRowNames);
        Assert.Empty(d.LargestEventsFiles);
        Assert.False(d.Truncated);
    }

    [Fact]
    public void Doctor_never_writes_or_deletes_anything()
    {
        using var fx = MixedStore();
        // A first read-only open of a WAL database can create SQLite's own -wal/-shm side files; do that before the snapshot.
        new ChatStore(fx.Paths).Stats();
        var before = Snapshot(fx.Root);
        var dbBefore = File.ReadAllBytes(fx.DbPath);

        new ChatStore(fx.Paths).Doctor();
        Run(fx, "doctor", "--text");

        Assert.Equal(before, Snapshot(fx.Root));
        Assert.Equal(dbBefore, File.ReadAllBytes(fx.DbPath));
    }

    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("-shm", StringComparison.Ordinal))
            .Select(p => File.Exists(p)
                ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}"
                : $"{p}|dir")
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Doctor_needs_a_store()
    {
        using var fx = new FixtureStore(createDb: false);
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Doctor());
        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
    }

    // ---- CLI ----

    private static (int Exit, string Out) Run(FixtureStore fx, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), o, e);
        return (exit, o.ToString());
    }

    [Fact]
    public void Cli_doctor_prints_snake_case_json()
    {
        using var fx = MixedStore();

        var (exit, output) = Run(fx, "doctor");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("doctor", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("truncated").ValueKind);
        Assert.Equal(3, root.GetProperty("session_rows").GetInt32());
        Assert.Equal(3, root.GetProperty("session_folders").GetInt32());
        Assert.Equal(1, root.GetProperty("rows_without_folder").GetInt32());
        Assert.Equal(C, root.GetProperty("rows_without_folder_ids")[0].GetString());
        Assert.Equal(1, root.GetProperty("folders_without_row").GetInt32());
        Assert.Equal(D, root.GetProperty("folders_without_row_names")[0].GetString());
        Assert.Equal(2, root.GetProperty("sessions_without_turns").GetInt32());
        Assert.Equal(2, root.GetProperty("sessions_without_usage").GetInt32());
        var largest = root.GetProperty("largest_events_files")[0];
        Assert.Equal(D, largest.GetProperty("session_id").GetString());
        Assert.Equal(300, largest.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public void Cli_doctor_takes_no_arguments()
    {
        using var fx = MixedStore();
        Assert.Equal(ExitCodes.Usage, Run(fx, "doctor", A).Exit);
    }

    [Fact]
    public void Cli_doctor_text_shows_counts_and_coverage()
    {
        using var fx = MixedStore();

        var (exit, output) = Run(fx, "doctor", "--text");

        Assert.Equal(0, exit);
        Assert.Contains("doctor (read-only)", output);
        Assert.Contains("session rows: 3", output);
        Assert.Contains("rows without a folder: 1", output);
        Assert.Contains("folders without a row: 1", output);
        Assert.Contains("sessions without turns: 2", output);
        Assert.Contains("sessions without usage rows: 2", output);
        Assert.Contains("300 bytes  " + D, output);
        Assert.Contains("coverage: session-store.db only", output);
    }

    [Fact]
    public void Text_output_cleans_format_characters_in_folder_names()
    {
        using var fx = new FixtureStore();
        Directory.CreateDirectory(Path.Combine(fx.SessionStateDir, "evil\u202Egnp.exe"));

        var (exit, output) = Run(fx, "doctor", "--text");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u202E", output, StringComparison.Ordinal);
        Assert.Contains("evil?gnp.exe", output);
    }
}
