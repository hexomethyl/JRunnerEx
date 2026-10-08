using JRunner.Core.Configuration;
using Xunit;

namespace JRunner.Core.Tests.Configuration;

public sealed class IniParserTests
{
    [Fact]
    public void Parse_handles_comments_whitespace_labels_properties_and_literals()
    {
        const string text = "  ; comment kept as a comment\n\n[ xenon bl ]\npatch smc = false\n..\\launch.xex\n";

        var result = IniParser.Parse(text);

        Assert.False(result.HasErrors);
        Assert.Equal(new[] { "xenonbl" }, result.Document.Labels);

        var property = Assert.Single(result.Document.GetProperties("xenon bl"));
        Assert.Equal("patchsmc", property.Key);
        Assert.Equal("false", property.Value);

        var literal = Assert.Single(result.Document.GetLiterals("xenonbl"));
        Assert.Equal(@"..\launch.xex", literal.Text);
        Assert.IsType<IniCommentLine>(result.Document.Lines[0]);
        Assert.IsType<IniBlankLine>(result.Document.Lines[1]);
    }

    [Fact]
    public void Editor_preserves_sections_comments_and_literals_through_a_parse_round_trip()
    {
        const string text = "; preserved\n[retail bl] ; section comment\npatchsmc = true\n..\\launch.xex\n";
        var parsed = IniParser.Parse(text);

        var edited = parsed.Document
            .WithValue("retailbl", "patch smc", "false")
            .WithLiteral("retailbl", @"..\lhelper.xex")
            .WithoutLiteral("retailbl", @"..\launch.xex");
        var roundTrip = IniParser.Parse(edited.ToText("\r\n"));

        Assert.False(roundTrip.HasErrors);
        Assert.True(roundTrip.Document.TryGetValue("retail bl", "patchsmc", out var value));
        Assert.Equal("false", value);
        Assert.Equal(
            new[] { @"..\lhelper.xex" },
            roundTrip.Document.GetLiterals("retailbl").Select(literal => literal.Text));
        Assert.IsType<IniCommentLine>(roundTrip.Document.Lines[0]);
        Assert.Equal(
            "section comment",
            Assert.IsType<IniSectionLine>(roundTrip.Document.Lines[1]).Comment);
    }

    [Fact]
    public void Parse_reports_malformed_sections_and_empty_keys_without_console_side_effects()
    {
        var result = IniParser.Parse("[broken\n = value\n");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == IniDiagnosticKind.MissingSectionTerminator);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == IniDiagnosticKind.EmptyKey);
    }
}
