using System.Text;
using Temno.Core.Agent.Skills;
using Xunit;

namespace Temno.Core.Tests;

// WP-309 D-1: SKILL.md follows the Agent Skills spec, and every refusal says what to fix.
public sealed class SkillDocumentTests
{
    private static string Skill(string front, string body = "Do the thing.") => "---\n" + front + "\n---\n\n" + body + "\n";

    private static SkillDocument Ok(string text, string? folder = null)
    {
        var result = SkillDocument.Parse(text, folder);
        Assert.True(result.Ok, result.Error);
        return result.Document!;
    }

    private static string Refused(string text, string? folder = null)
    {
        var result = SkillDocument.Parse(text, folder);
        Assert.False(result.Ok);
        return result.Error!;
    }

    [Fact]
    public void A_minimal_skill_parses_and_its_body_is_everything_after_the_frontmatter()
    {
        var doc = Ok(Skill("name: podcast-cleanup\ndescription: Clean up a recorded conversation.", "## Steps\n1. Trim.\n"), "podcast-cleanup");
        Assert.Equal("podcast-cleanup", doc.Name);
        Assert.Equal("Clean up a recorded conversation.", doc.Description);
        Assert.Equal("## Steps\n1. Trim.", doc.Body);
        Assert.Empty(doc.IgnoredFields);
    }

    [Theory]
    [InlineData("PDF-Processing", "only lowercase letters a-z, digits and hyphens")]
    [InlineData("-pdf", "cannot start or end with a hyphen")]
    [InlineData("pdf-", "cannot start or end with a hyphen")]
    [InlineData("pdf--processing", "two hyphens in a row")]
    [InlineData("pdf processing", "only lowercase letters a-z, digits and hyphens")]
    [InlineData("pdf_processing", "only lowercase letters a-z, digits and hyphens")]
    [InlineData("caf\u00e9", "only lowercase letters a-z, digits and hyphens")]
    [InlineData("../evil", "only lowercase letters a-z, digits and hyphens")]
    public void A_bad_name_is_refused_with_the_rule_it_breaks(string name, string rule)
    {
        Assert.Contains(rule, Refused(Skill($"name: {name}\ndescription: x")), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_follow_the_length_limit()
    {
        Ok(Skill($"name: {new string('a', 64)}\ndescription: x"));
        Assert.Equal("The name is 65 characters; the limit is 64.", Refused(Skill($"name: {new string('a', 65)}\ndescription: x")));
        Assert.True(SkillDocument.IsValidName("a"));
        Assert.True(SkillDocument.IsValidName("a-1-b"));
        Assert.False(SkillDocument.IsValidName(""));
        Assert.False(SkillDocument.IsValidName(null));
    }

    [Fact]
    public void A_missing_or_empty_field_is_refused()
    {
        Assert.Equal("SKILL.md needs a name.", Refused(Skill("description: x")));
        Assert.Equal("SKILL.md needs a name.", Refused(Skill("name:\ndescription: x")));
        Assert.Equal("SKILL.md needs a description.", Refused(Skill("name: a")));
        Assert.Equal("SKILL.md needs a description.", Refused(Skill("name: a\ndescription: \"  \"")));
        // T2 round 1, F5: a comment is not a value, as YAML reads it.
        Assert.Equal("SKILL.md needs a description.", Refused(Skill("name: a\ndescription: # comment")));
        Assert.Equal("real text", Ok(Skill("name: a\ndescription: # comment\n  real text")).Description);
    }

    [Fact]
    public void An_oversized_field_is_refused_and_the_limits_count_characters()
    {
        Ok(Skill("name: a\ndescription: " + new string('d', 1024)));
        Assert.Equal("The description is 1,025 characters; the limit is 1,024.", Refused(Skill("name: a\ndescription: " + new string('d', 1025))));
        // A character outside the BMP is one character, as the spec counts it.
        Ok(Skill("name: a\ndescription: " + string.Concat(Enumerable.Repeat("\U0001F3AC", 1024))));
        Ok(Skill("name: a\ndescription: x\ncompatibility: " + new string('c', 500)));
        Assert.Contains("compatibility field is 501 characters", Refused(Skill("name: a\ndescription: x\ncompatibility: " + new string('c', 501))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_differs_from_its_folder_is_refused()
    {
        Assert.Equal(
            "The name 'podcast-cleanup' does not match its folder 'podcast-cleanup-main'. A skill's name and folder must be the same.",
            Refused(Skill("name: podcast-cleanup\ndescription: x"), "podcast-cleanup-main"));
    }

    [Fact]
    public void A_file_over_64_KB_is_refused_before_it_is_decoded()
    {
        var front = Encoding.UTF8.GetBytes(Skill("name: a\ndescription: x", ""));
        var exact = front.Concat(Enumerable.Repeat((byte)'x', SkillDocument.MaxFileBytes - front.Length)).ToArray();
        Assert.True(SkillDocument.Parse(exact, null).Ok);
        var over = exact.Append((byte)'x').ToArray();
        Assert.Equal("SKILL.md is 65 KB; the limit is 64 KB.", SkillDocument.Parse(over, null).Error);
    }

    [Fact]
    public void Text_that_is_not_a_skill_file_is_refused_plainly()
    {
        Assert.Equal("SKILL.md is not UTF-8 text.", SkillDocument.Parse([0x2D, 0x2D, 0x2D, 0x0A, 0xFF, 0xFE], null).Error);
        Assert.Contains("must start with a --- line", Refused("name: a\ndescription: x\n"), StringComparison.Ordinal);
        Assert.Equal("The frontmatter has no closing --- line.", Refused("---\nname: a\ndescription: x\n"));
        Assert.Contains("NUL character", Refused(Skill("name: a\ndescription: x", "a\0b")), StringComparison.Ordinal);
        Assert.Equal("Line 3 of SKILL.md sets 'name' a second time.", Refused(Skill("name: a\nname: b\ndescription: x")));
        Assert.Equal("Line 2 of SKILL.md needs a space after the colon.", Refused(Skill("name:a\ndescription: x")));
        Assert.Equal("Line 3 of SKILL.md is not a 'field: value' line.", Refused(Skill("name: a\njust words\ndescription: x")));
        Assert.Contains("anchors, aliases or tags", Refused(Skill("name: a\ndescription: !!str tagged")), StringComparison.Ordinal);
        Assert.Contains("must be text", Refused(Skill("name: a\ndescription:\n  - one\n  - two")), StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_order_mark_and_CRLF_are_accepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("---\r\nname: a\r\ndescription: x\r\n---\r\nBody\r\n")).ToArray();
        var doc = SkillDocument.Parse(bytes, "a").Document!;
        Assert.Equal("x", doc.Description);
        Assert.Equal("Body", doc.Body);
    }

    [Fact]
    public void Quoted_folded_and_literal_descriptions_read_as_YAML_does()
    {
        Assert.Equal("Use when: the user asks", Ok(Skill("name: a\ndescription: \"Use when: the user asks\"")).Description);
        Assert.Equal("It's \"quoted\"", Ok(Skill("name: a\ndescription: 'It''s \"quoted\"'")).Description);
        Assert.Equal("tab\there \u00e9", Ok(Skill("name: a\ndescription: \"tab\\there \\u00e9\"")).Description);
        Assert.Equal("one two three", Ok(Skill("name: a\ndescription: >-\n  one\n  two\n  three")).Description);
        Assert.Equal("line one\nline two", Ok(Skill("name: a\ndescription: |\n  line one\n  line two\n")).Description);
        Assert.Equal("spans two lines", Ok(Skill("name: a\ndescription: spans\n  two lines")).Description);
        Assert.Equal("value", Ok(Skill("name: a\ndescription: value # a comment")).Description);
        Assert.Equal("C# helper", Ok(Skill("name: a\ndescription: C# helper")).Description);
        Assert.Equal("a", Ok(Skill("# leading comment\nname: \"a\"\ndescription: x")).Name);
        Assert.Contains("no closing quote", Refused(Skill("name: a\ndescription: \"open")), StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_fields_are_read_and_unknown_ones_are_named_not_refused()
    {
        var doc = Ok(Skill("""
            name: pdf-processing
            description: Extract PDF text.
            license: Apache-2.0
            compatibility: Designed for Temno
            metadata:
              author: example-org
              version: "1.0"
            allowed-tools: Bash(git:*) Read
            disable-model-invocation: true
            argument-hint:
            - one
            """));
        Assert.Equal("Apache-2.0", doc.License);
        Assert.Equal("Designed for Temno", doc.Compatibility);
        Assert.Equal("example-org", doc.Metadata["author"]);
        Assert.Equal("1.0", doc.Metadata["version"]);
        Assert.Equal(new[] { "allowed-tools", "disable-model-invocation", "argument-hint" }, doc.IgnoredFields);
    }

    [Fact]
    public void Shown_drops_control_characters_and_shortens()
    {
        Assert.Equal("a?b", SkillDocument.Shown("a\nb"));
        Assert.Equal(new string('x', 64) + "\u2026", SkillDocument.Shown(new string('x', 100)));
    }
}
