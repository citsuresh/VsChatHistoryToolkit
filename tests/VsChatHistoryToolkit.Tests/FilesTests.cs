using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class FilesTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";

    private static FixtureStore StoreWithFiles()
    {
        var fx = new FixtureStore();
        fx.AddSession(A, summary: "chat a");
        fx.AddSession(B, summary: "chat b");
        fx.AddSession(C, summary: "chat c");
        // A: inserted out of turn order, so the listing must sort by turn first and row id second.
        fx.AddFile(A, @"C:\src\repo\Core\Store.cs", "edit", 2, "2026-01-05T10:20:00.000Z");
        fx.AddFile(A, @"C:\src\repo\Core\Caps.cs", "create", 0, "2026-01-05T10:00:00.000Z");
        fx.AddFile(A, @"C:\src\repo\README.md", "edit", 0, "2026-01-05T10:01:00.000Z");
        // B touched Store.cs later than A did.
        fx.AddFile(B, @"C:\src\repo\Core\STORE.cs", "edit", 4, "2026-02-01T09:00:00.000Z");
        fx.AddFile(B, @"C:\src\repo\Core\Store.Tests.cs", "create", 1, "2026-02-01T08:00:00.000Z");
        return fx;
    }

    // ---- files <id> ----

    [Fact]
    public void Files_lists_the_files_of_one_chat_by_turn_then_row()
    {
        using var fx = StoreWithFiles();

        var f = new ChatStore(fx.Paths).Files(A, null);

        Assert.True(f.Recorded);
        Assert.Null(f.Note);
        Assert.Equal(new[] { @"C:\src\repo\Core\Caps.cs", @"C:\src\repo\README.md", @"C:\src\repo\Core\Store.cs" },
            f.Files.Select(x => x.FilePath));
        Assert.Equal(new long?[] { 0, 0, 2 }, f.Files.Select(x => x.TurnIndex));
        Assert.Equal("create", f.Files[0].ToolName);
        Assert.Equal("2026-01-05T10:00:00.000Z", f.Files[0].FirstSeenAt);
        Assert.Equal(3, f.Count);
        Assert.Equal(Caps.DefaultLimit, f.Limit);
        Assert.False(f.Truncated);
        Assert.Equal(Caps.Coverage, f.Coverage);
    }

    [Fact]
    public void Files_is_capped_by_limit_and_flagged()
    {
        using var fx = StoreWithFiles();

        var f = new ChatStore(fx.Paths).Files(A, 2);

        Assert.Equal(2, f.Count);
        Assert.True(f.Truncated);
    }

    [Fact]
    public void Files_limit_above_the_maximum_is_clamped_and_flagged()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        for (var i = 0; i < Caps.MaxLimit + 3; i++) fx.AddFile(A, $@"C:\src\f{i:D3}.cs", turn: i);

        var f = new ChatStore(fx.Paths).Files(A, 1000);

        Assert.Equal(Caps.MaxLimit, f.Count);
        Assert.Equal(Caps.MaxLimit, f.Limit);
        Assert.True(f.Truncated);
    }

    [Fact]
    public void Files_caps_long_paths_and_flags_it()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddFile(A, @"C:\" + new string('x', Caps.FilePathChars + 50) + ".cs");

        var f = new ChatStore(fx.Paths).Files(A, null);

        Assert.Equal(Caps.FilePathChars + 1, f.Files[0].FilePath.Length);
        Assert.EndsWith("…", f.Files[0].FilePath);
        Assert.True(f.Truncated);
    }

    [Fact]
    public void Files_for_a_chat_without_rows_is_a_normal_answer()
    {
        using var fx = StoreWithFiles();

        var f = new ChatStore(fx.Paths).Files(C, null);

        Assert.False(f.Recorded);
        Assert.Empty(f.Files);
        Assert.Contains("No edited or created files", f.Note);
        Assert.Equal(Caps.Coverage, f.Coverage);
    }

    [Fact]
    public void Files_without_the_table_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.Exec("DROP TABLE session_files");

        var f = new ChatStore(fx.Paths).Files(A, null);

        Assert.False(f.Recorded);
        Assert.Contains("no session_files table", f.Note);
    }

    [Fact]
    public void Files_with_a_table_of_another_shape_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.Exec("DROP TABLE session_files");
        fx.Exec("CREATE TABLE session_files(session_id TEXT, path TEXT)");
        fx.Exec("INSERT INTO session_files VALUES($s, 'C:\\a.cs')", ("$s", A));

        Assert.False(new ChatStore(fx.Paths).Files(A, null).Recorded);
        Assert.False(new ChatStore(fx.Paths).FilesByPath("a.cs", null).Recorded);
    }

    [Fact]
    public void Files_for_an_unknown_session_is_not_found_even_without_the_table()
    {
        using var fx = StoreWithFiles();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Files("99999999-9999-9999-9999-999999999999", null));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);

        fx.Exec("DROP TABLE session_files");
        ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Files("99999999-9999-9999-9999-999999999999", null));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Files_rejects_a_malformed_id()
    {
        using var fx = StoreWithFiles();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Files("..\\evil", null));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    [Fact]
    public void Files_rejects_a_limit_below_one()
    {
        using var fx = StoreWithFiles();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Files(A, 0));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    // ---- files --path ----

    [Fact]
    public void Files_by_path_groups_matching_chats_newest_first_case_insensitively()
    {
        using var fx = StoreWithFiles();

        var r = new ChatStore(fx.Paths).FilesByPath("core\\store", null);

        Assert.True(r.Recorded);
        Assert.Equal(new[] { B, A }, r.Sessions.Select(s => s.SessionId));
        Assert.Equal(2, r.Sessions[0].FileCount);
        Assert.Equal(4, r.Sessions[0].LastTurn);
        Assert.Equal("2026-02-01T09:00:00.000Z", r.Sessions[0].LastSeenAt);
        Assert.Equal("chat b", r.Sessions[0].Title);
        Assert.Equal("summary", r.Sessions[0].TitleSource);
        Assert.Equal(1, r.Sessions[1].FileCount);
        Assert.Equal(2, r.Sessions[1].LastTurn);
        Assert.Equal("core\\store", r.Path);
        Assert.False(r.Truncated);
        Assert.Equal(Caps.Coverage, r.Coverage);
    }

    [Fact]
    public void Files_by_path_counts_only_matching_files()
    {
        using var fx = StoreWithFiles();

        var r = new ChatStore(fx.Paths).FilesByPath("README", null);

        var only = Assert.Single(r.Sessions);
        Assert.Equal(A, only.SessionId);
        Assert.Equal(1, only.FileCount);
        Assert.Equal(0, only.LastTurn);
    }

    [Fact]
    public void Files_by_path_prefers_the_workspace_name_as_title()
    {
        using var fx = StoreWithFiles();
        fx.WriteYaml(A, FixtureStore.SampleYaml(A, "Named chat"));

        var r = new ChatStore(fx.Paths).FilesByPath("README", null);

        Assert.Equal("Named chat", r.Sessions[0].Title);
        Assert.Equal("name", r.Sessions[0].TitleSource);
    }

    [Fact]
    public void Files_by_path_treats_like_wildcards_literally()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddSession(B);
        fx.AddFile(A, @"C:\src\my_file%1.cs");
        fx.AddFile(B, @"C:\src\myXfileX1.cs");

        Assert.Equal(new[] { A }, new ChatStore(fx.Paths).FilesByPath("my_file%", null).Sessions.Select(s => s.SessionId));
        Assert.Empty(new ChatStore(fx.Paths).FilesByPath("src\\\\", null).Sessions);
        Assert.Equal(2, new ChatStore(fx.Paths).FilesByPath("src\\", null).Count);
    }

    [Fact]
    public void Files_listing_returns_a_path_exactly_as_stored_so_it_works_as_a_path_query()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddFile(A, @"C:\src\my  file.cs");

        var listed = new ChatStore(fx.Paths).Files(A, null).Files.Single().FilePath;

        Assert.Equal(@"C:\src\my  file.cs", listed);
        Assert.Equal(new[] { A }, new ChatStore(fx.Paths).FilesByPath(listed, null).Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Files_by_path_echoes_and_searches_the_text_as_given()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddSession(B);
        fx.AddFile(A, @"C:\src\my  file.cs");
        fx.AddFile(B, @"C:\src\my file.cs");

        var r = new ChatStore(fx.Paths).FilesByPath("  my  file ", null);

        Assert.Equal("my  file", r.Path);
        Assert.Equal(new[] { A }, r.Sessions.Select(s => s.SessionId));
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Files_by_path_caps_a_long_query_echo_and_flags_it()
    {
        using var fx = StoreWithFiles();

        var r = new ChatStore(fx.Paths).FilesByPath(new string('q', Caps.FilePathChars + 10), null);

        Assert.Equal(Caps.FilePathChars + 1, r.Path.Length);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Files_by_path_without_matches_is_an_empty_answer()
    {
        using var fx = StoreWithFiles();

        var r = new ChatStore(fx.Paths).FilesByPath("nothing-like-this", null);

        Assert.True(r.Recorded);
        Assert.Empty(r.Sessions);
    }

    [Fact]
    public void Files_by_path_is_capped_by_limit_and_flagged()
    {
        using var fx = StoreWithFiles();

        var r = new ChatStore(fx.Paths).FilesByPath("store", 1);

        Assert.Equal(new[] { B }, r.Sessions.Select(s => s.SessionId));
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Files_by_path_without_the_table_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.Exec("DROP TABLE session_files");

        var r = new ChatStore(fx.Paths).FilesByPath("store", null);

        Assert.False(r.Recorded);
        Assert.Contains("no session_files table", r.Note);
    }

    [Fact]
    public void Files_by_path_needs_some_text()
    {
        using var fx = StoreWithFiles();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).FilesByPath("   ", null));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    [Fact]
    public void Files_does_not_write_to_the_store()
    {
        using var fx = StoreWithFiles();
        var before = File.ReadAllBytes(fx.DbPath);
        new ChatStore(fx.Paths).Files(A, null);
        new ChatStore(fx.Paths).FilesByPath("store", null);
        Assert.Equal(before, File.ReadAllBytes(fx.DbPath));
    }

    // ---- CLI ----

    private static (int Exit, string Out, string Err) Run(FixtureStore fx, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), o, e);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_files_prints_snake_case_json()
    {
        using var fx = StoreWithFiles();

        var (exit, output, _) = Run(fx, "files", A, "--limit", "5");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("files", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("truncated").ValueKind);
        Assert.True(root.GetProperty("recorded").GetBoolean());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        var first = root.GetProperty("files")[0];
        Assert.Equal(@"C:\src\repo\Core\Caps.cs", first.GetProperty("file_path").GetString());
        Assert.Equal("create", first.GetProperty("tool_name").GetString());
        Assert.Equal(0, first.GetProperty("turn_index").GetInt64());
        Assert.Equal("2026-01-05T10:00:00.000Z", first.GetProperty("first_seen_at").GetString());
    }

    [Fact]
    public void Cli_files_by_path_prints_snake_case_json()
    {
        using var fx = StoreWithFiles();

        var (exit, output, _) = Run(fx, "files", "--path", "store");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("files", root.GetProperty("command").GetString());
        Assert.Equal("store", root.GetProperty("path").GetString());
        var first = root.GetProperty("sessions")[0];
        Assert.Equal(B, first.GetProperty("session_id").GetString());
        Assert.Equal(2, first.GetProperty("file_count").GetInt32());
        Assert.Equal(4, first.GetProperty("last_turn").GetInt64());
        Assert.Equal("chat b", first.GetProperty("title").GetString());
    }

    [Fact]
    public void Cli_files_needs_exactly_one_of_id_or_path()
    {
        using var fx = StoreWithFiles();
        Assert.Equal(ExitCodes.Usage, Run(fx, "files").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "files", A, B).Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "files", A, "--path", "store").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "files", "--path", "").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "files", "--path").Exit);
    }

    [Fact]
    public void Cli_files_for_an_unknown_session_exits_with_not_found()
    {
        using var fx = StoreWithFiles();
        Assert.Equal(ExitCodes.NotFound, Run(fx, "files", "99999999-9999-9999-9999-999999999999").Exit);
    }

    [Fact]
    public void Cli_files_without_rows_is_a_normal_answer()
    {
        using var fx = StoreWithFiles();

        var (exit, output, _) = Run(fx, "files", C);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("recorded").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public void Cli_files_text_mentions_coverage()
    {
        using var fx = StoreWithFiles();

        var byId = Run(fx, "files", A, "--text").Out;
        var byPath = Run(fx, "files", "--path", "store", "--text").Out;

        Assert.Contains("files: 3 for " + A, byId);
        Assert.Contains("README.md", byId);
        Assert.Contains("coverage: session-store.db only", byId);
        Assert.Contains("files: 2 chat(s) touched a path containing \"store\"", byPath);
        Assert.Contains("2 file(s)  last turn 4  chat b", byPath);
        Assert.Contains("coverage: session-store.db only", byPath);
    }

    [Fact]
    public void Cli_files_text_shows_the_note_when_nothing_is_recorded()
    {
        using var fx = StoreWithFiles();

        var output = Run(fx, "files", C, "--text").Out;

        Assert.Contains("No edited or created files", output);
    }

    [Fact]
    public void Text_output_cleans_control_characters_in_paths_tools_and_titles()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "title\u001b]0;pwned\u0007\nFAKE: title");
        fx.AddFile(A, "C:\\evil\u001b[31mfile\nFAKE: line.cs", tool: "ed\u001b[2Jit");

        var byId = Run(fx, "files", A, "--text").Out;
        var byPath = Run(fx, "files", "--path", "evil", "--text").Out;

        foreach (var output in new[] { byId, byPath })
        {
            Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0007", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\nFAKE:", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Text_errors_for_a_bad_id_do_not_echo_control_characters()
    {
        using var fx = StoreWithFiles();

        var (exit, _, err) = Run(fx, "files", "\u001b[31m", "--text");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.DoesNotContain("\u001b", err, StringComparison.Ordinal);
    }
}
