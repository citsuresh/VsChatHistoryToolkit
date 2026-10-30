using System.Text;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Tests;

public class YamlAndTitleTests
{
    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    [Theory]
    [InlineData("id: x\nname: Old\nuser_named: false\n")]
    [InlineData("id: x\r\nname: Old\r\nuser_named: false\r\n")]
    [InlineData("id: x\nname: Old\nuser_named: false")]
    [InlineData("")]
    [InlineData("\n\n# comment\nnested:\n  name: not top level\n")]
    [InlineData("name: caf\u00e9 \u2603\n")]
    public void Parse_then_write_is_byte_identical(string text)
    {
        var bytes = Utf8(text);
        Assert.Equal(bytes, WorkspaceYaml.Parse(bytes, strict: true).ToBytes());
    }

    [Fact]
    public void Bom_is_preserved()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("id: x\nname: Old\n")).ToArray();
        var yaml = WorkspaceYaml.Parse(bytes, strict: true);
        Assert.Equal(bytes, yaml.ToBytes());

        yaml.SetLine("name", "name: New");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("id: x\nname: New\n")).ToArray(), yaml.ToBytes());
    }

    [Fact]
    public void Invalid_utf8_is_refused_when_strict()
    {
        var ex = Assert.Throws<ToolkitException>(() => WorkspaceYaml.Parse(new byte[] { 0x6E, 0xFF, 0xFE }, strict: true));
        Assert.Equal(ErrorKind.Refused, ex.Kind);
        WorkspaceYaml.Parse(new byte[] { 0x6E, 0xFF, 0xFE }, strict: false);
    }

    [Fact]
    public void Nested_keys_are_not_matched()
    {
        var yaml = WorkspaceYaml.Parse(Utf8("a:\n  name: inner\n"), strict: true);
        Assert.Null(yaml.GetLine("name"));
    }

    [Theory]
    [InlineData("id: x\nbranch: main\n")]
    [InlineData("id: x\nbranch: main")]
    [InlineData("id: x\r\nbranch: main\r\n")]
    [InlineData("")]
    public void Insert_then_remove_restores_exact_bytes(string text)
    {
        var bytes = Utf8(text);
        var yaml = WorkspaceYaml.Parse(bytes, strict: true);
        yaml.SetLine("name", "name: T");
        yaml.SetLine("user_named", "user_named: true");
        Assert.Equal("T", yaml.GetValue("name"));

        yaml.RemoveLine("user_named");
        yaml.RemoveLine("name");
        Assert.Equal(bytes, yaml.ToBytes());
    }

    [Theory]
    [InlineData("Plain title", "Plain title")]
    [InlineData("Don't stop", "Don't stop")]
    [InlineData("Fix: the bug", "\"Fix: the bug\"")]
    [InlineData("Use #1 tag", "\"Use #1 tag\"")]
    [InlineData("Say \"hi\"", "\"Say \\\"hi\\\"\"")]
    [InlineData("C:\\temp\\x", "\"C:\\\\temp\\\\x\"")]
    [InlineData("-leading dash", "\"-leading dash\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("123", "\"123\"")]
    [InlineData("1.5e3", "\"1.5e3\"")]
    [InlineData("[list] item", "\"[list] item\"")]
    [InlineData("*star", "\"*star\"")]
    [InlineData("trailing colon:", "\"trailing colon:\"")]
    public void Yaml_value_quoting(string title, string expected)
    {
        Assert.Equal(expected, TitleRules.FormatYamlValue(title));
    }

    [Theory]
    [InlineData("Plain title")]
    [InlineData("Fix: the bug # now")]
    [InlineData("Say \"hi\" \\ there")]
    [InlineData("C:\\temp\\x")]
    [InlineData("caf\u00e9 \u2603 \u65e5\u672c\u8a9e")]
    [InlineData("'single' and \"double\"")]
    [InlineData("yes")]
    public void Quoted_titles_read_back_unchanged(string title)
    {
        var line = "name: " + TitleRules.FormatYamlValue(title);
        Assert.Equal(title, WorkspaceYaml.ValueOf(line, "name"));
    }

    [Fact]
    public void Unquote_handles_single_quotes_and_comments()
    {
        Assert.Equal("it's", WorkspaceYaml.Unquote("'it''s'"));
        Assert.Equal("value", WorkspaceYaml.Unquote("  value   # note"));
        Assert.Equal("a # b", WorkspaceYaml.Unquote("\"a # b\""));
    }

    [Fact]
    public void Title_normalize_trims_and_strips_control_characters()
    {
        Assert.Equal("ab c", TitleRules.Normalize("  a\u0007b\nc  "));
        Assert.Equal("tab here", TitleRules.Normalize("tab\there"));
        Assert.Equal("zero width", TitleRules.Normalize("zero\u200B width"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001\u0002")]
    public void Title_empty_is_refused(string? raw)
    {
        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => TitleRules.Normalize(raw)).Kind);
    }

    [Fact]
    public void Title_length_limit()
    {
        Assert.Equal(120, TitleRules.Normalize(new string('a', 120)).Length);
        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => TitleRules.Normalize(new string('a', 121))).Kind);
    }
}
