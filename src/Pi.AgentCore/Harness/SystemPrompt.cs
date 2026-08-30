using System.Text;

namespace Pi.AgentCore.Harness;

/// <summary>Formats the model-visible skill section of the harness system prompt.</summary>
public static class SystemPrompt
{
    /// <summary>
    /// Formats visible skills in the order supplied, omitting skills that disable model
    /// invocation and escaping XML-sensitive metadata fields.
    /// </summary>
    public static string FormatSkillsForSystemPrompt(IReadOnlyList<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        var visibleSkills = skills.Where(static skill => !skill.DisableModelInvocation).ToArray();
        if (visibleSkills.Length == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "The following skills provide specialized instructions for specific tasks.",
            "Read the full skill file when the task matches its description.",
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.",
            string.Empty,
            "<available_skills>",
        };

        foreach (var skill in visibleSkills)
        {
            ArgumentNullException.ThrowIfNull(skill);
            lines.Add("  <skill>");
            lines.Add($"    <name>{EscapeXml(skill.Name)}</name>");
            lines.Add($"    <description>{EscapeXml(skill.Description)}</description>");
            lines.Add($"    <location>{EscapeXml(skill.FilePath)}</location>");
            lines.Add("  </skill>");
        }

        lines.Add("</available_skills>");
        return string.Join('\n', lines);
    }

    private static string EscapeXml(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&apos;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>Compatibility facade for the system-prompt formatting helper.</summary>
public static class SystemPromptUtilities
{
    /// <inheritdoc cref="SystemPrompt.FormatSkillsForSystemPrompt" />
    public static string FormatSkillsForSystemPrompt(IReadOnlyList<Skill> skills) =>
        SystemPrompt.FormatSkillsForSystemPrompt(skills);
}
