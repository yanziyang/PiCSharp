using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Utils;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class ResourceFormattingTests
{
    [Fact(DisplayName = "formats skill invocations with additional instructions")]
    public void Formats_skill_invocations_with_additional_instructions()
    {
        var skill = new Skill
        {
            Name = "inspect",
            Description = "Inspect things",
            Content = "Use inspection tools.",
            FilePath = "/project/.pi/skills/inspect/SKILL.md",
        };

        Assert.Equal(
            "<skill name=\"inspect\" location=\"/project/.pi/skills/inspect/SKILL.md\">\n" +
            "References are relative to /project/.pi/skills/inspect.\n\n" +
            "Use inspection tools.\n</skill>\n\nCheck errors.",
            SkillLoader.FormatSkillInvocation(skill, "Check errors."));
    }

    [Fact(DisplayName = "formats prompt template invocations with positional arguments")]
    public void Formats_prompt_template_invocations_with_positional_arguments()
    {
        var template = new PromptTemplate { Name = "review", Content = "Review $1 with $ARGUMENTS" };

        Assert.Equal(
            "Review a.ts with a.ts care",
            PromptTemplateUtilities.FormatPromptTemplateInvocation(template, ["a.ts", "care"]));
    }
}
