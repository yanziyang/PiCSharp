using Pi.AgentCore.Harness;

using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class SystemPromptTests
{
    [Fact(DisplayName = "formats visible skills in order and skips model-disabled skills")]
    public void Formats_visible_skills_in_order_and_skips_model_disabled_skills()
    {
        var visibleSkill = new Skill
        {
            Name = "visible",
            Description = "Use <this> & that",
            Content = "visible content",
            FilePath = "/skills/visible/SKILL.md",
        };
        var secondSkill = new Skill
        {
            Name = "second",
            Description = "Second skill",
            Content = "second content",
            FilePath = "/skills/second/SKILL.md",
        };
        var disabledSkill = new Skill
        {
            Name = "hidden",
            Description = "Hidden",
            Content = "hidden content",
            FilePath = "/skills/hidden/SKILL.md",
            DisableModelInvocation = true,
        };

        Assert.Equal(
            "The following skills provide specialized instructions for specific tasks.\n" +
            "Read the full skill file when the task matches its description.\n" +
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.\n" +
            "\n<available_skills>\n" +
            "  <skill>\n" +
            "    <name>visible</name>\n" +
            "    <description>Use &lt;this&gt; &amp; that</description>\n" +
            "    <location>/skills/visible/SKILL.md</location>\n" +
            "  </skill>\n" +
            "  <skill>\n" +
            "    <name>second</name>\n" +
            "    <description>Second skill</description>\n" +
            "    <location>/skills/second/SKILL.md</location>\n" +
            "  </skill>\n" +
            "</available_skills>",
            SystemPrompt.FormatSkillsForSystemPrompt([visibleSkill, disabledSkill, secondSkill]));
    }

    [Fact(DisplayName = "returns an empty string when no skills are model-visible")]
    public void Returns_an_empty_string_when_no_skills_are_model_visible()
    {
        var disabledSkill = new Skill
        {
            Name = "hidden",
            Description = "Hidden",
            Content = "hidden content",
            FilePath = "/skills/hidden/SKILL.md",
            DisableModelInvocation = true,
        };

        Assert.Equal(string.Empty, SystemPrompt.FormatSkillsForSystemPrompt([disabledSkill]));
    }

    [Fact(DisplayName = "escapes XML in all model-visible skill fields")]
    public void Escapes_xml_in_all_model_visible_skill_fields()
    {
        var skill = new Skill
        {
            Name = "a&b",
            Description = "Quote \"double\" and 'single'",
            Content = "content",
            FilePath = "/skills/<bad>&\"quote\"/SKILL.md",
        };

        Assert.Contains(
            "<name>a&amp;b</name>\n" +
            "    <description>Quote &quot;double&quot; and &apos;single&apos;</description>\n" +
            "    <location>/skills/&lt;bad&gt;&amp;&quot;quote&quot;/SKILL.md</location>",
            SystemPrompt.FormatSkillsForSystemPrompt([skill]),
            StringComparison.Ordinal);
    }
}
