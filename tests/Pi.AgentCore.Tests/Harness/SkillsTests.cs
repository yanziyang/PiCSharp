using System.Diagnostics.CodeAnalysis;

using Pi.AgentCore.Harness;

using Xunit;

namespace Pi.AgentCore.Tests.Harness;

[SuppressMessage("Usage", "xUnit1051", Justification = "Skill-loader tests use the deterministic in-memory execution environment.")]
public sealed class SkillsTests
{
    [Fact(DisplayName = "loads SKILL.md files through the execution environment")]
    public async Task Loads_skill_md_files_through_the_execution_environment()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory(".agents/skills/example");
        env.AddFile(
            ".agents/skills/example/SKILL.md",
            "---\nname: example\ndescription: Example skill\ndisable-model-invocation: true\n---\nUse this skill.\n");

        var result = await SkillLoader.LoadSkillsAsync(env, ".agents/skills");

        Assert.Empty(result.Diagnostics);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("example", skill.Name);
        Assert.Equal("Example skill", skill.Description);
        Assert.Equal("Use this skill.", skill.Content);
        Assert.Equal(env.Absolute(".agents/skills/example/SKILL.md"), skill.FilePath);
        Assert.True(skill.DisableModelInvocation);
    }

    [Fact(DisplayName = "loads skills through symlinked directories")]
    public async Task Loads_skills_through_symlinked_directories()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("actual/example");
        env.AddFile(
            "actual/example/SKILL.md",
            "---\nname: example\ndescription: Example skill\n---\nUse this skill.");
        env.AddSymlink("skills-link", "actual");

        var result = await SkillLoader.LoadSkillsAsync(env, "skills-link");

        var skill = Assert.Single(result.Skills);
        Assert.Equal("example", skill.Name);
        Assert.Equal(env.Absolute("skills-link/example/SKILL.md"), skill.FilePath);
    }

    [Fact(DisplayName = "preserves source info for sourced skills")]
    public async Task Preserves_source_info_for_sourced_skills()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("user/example");
        env.AddFile(
            "user/example/SKILL.md",
            "---\nname: example\ndescription: Example skill\n---\nUse this skill.");

        var result = await SkillLoader.LoadSourcedSkillsAsync<string>(
            env,
            [new SourcedSkillInput<string>("user", "user")]);

        Assert.Empty(result.Diagnostics);
        var sourced = Assert.Single(result.Skills);
        Assert.Equal("user", sourced.Source);
        Assert.Equal("example", sourced.Skill.Name);
        Assert.Equal("Example skill", sourced.Skill.Description);
        Assert.Equal("Use this skill.", sourced.Skill.Content);
        Assert.Equal(env.Absolute("user/example/SKILL.md"), sourced.Skill.FilePath);
        Assert.False(sourced.Skill.DisableModelInvocation);
    }

    [Fact(DisplayName = "attaches source info to diagnostics")]
    public async Task Attaches_source_info_to_diagnostics()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("user/broken");
        env.AddFile("user/broken/SKILL.md", "---\nname: broken\n---\nMissing description.");

        var result = await SkillLoader.LoadSourcedSkillsAsync<string>(
            env,
            [new SourcedSkillInput<string>("user", "user")]);

        Assert.Empty(result.Skills);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("warning", diagnostic.Type);
        Assert.Equal(SkillDiagnosticCodes.InvalidMetadata, diagnostic.Code);
        Assert.Equal("description is required", diagnostic.Message);
        Assert.Equal(env.Absolute("user/broken/SKILL.md"), diagnostic.Path);
        Assert.Equal("user", diagnostic.Source);
    }

    [Fact(DisplayName = "loads direct markdown children only from the root directory")]
    public async Task Loads_direct_markdown_children_only_from_the_root_directory()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("skills/nested");
        env.AddFile("skills/root.md", "---\ndescription: Root skill\n---\nRoot content");
        env.AddFile("skills/nested/ignored.md", "---\ndescription: Ignored\n---\nIgnored content");

        var result = await SkillLoader.LoadSkillsAsync(env, "skills");

        var skill = Assert.Single(result.Skills);
        Assert.Equal("skills", skill.Name);
        Assert.Equal("Root content", skill.Content);
        Assert.Equal(env.Absolute("skills/root.md"), skill.FilePath);
    }

    [Fact(DisplayName = "ignores root markdown docs that do not declare skills")]
    public async Task Ignores_root_markdown_docs_that_do_not_declare_skills()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("skills/nested-skill");
        env.AddFile("skills/README.md", "# Shared skills\n\nDocumentation.");
        env.AddFile("skills/AGENTS.md", "# Agent notes\n\nDocumentation.");
        env.AddFile("skills/CLAUDE.md", "---\ndescription: [invalid\n---\n\nDocumentation.");
        env.AddFile("skills/root.md", "---\ndescription: Root skill\n---\nRoot content");
        env.AddFile(
            "skills/nested-skill/SKILL.md",
            "---\nname: nested-skill\ndescription: Nested skill\n---\nNested content");

        var result = await SkillLoader.LoadSkillsAsync(env, "skills");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            ["nested-skill", "skills"],
            result.Skills.Select(static skill => skill.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "honors .gitignore, .ignore and .fdignore with negation and directory-only rules")]
    public async Task Honors_layered_ignore_files_with_negation_and_directory_only_rules()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("skills/ignore-dir");
        env.AddDirectory("skills/fd-dir");
        env.AddDirectory("skills/nested");
        env.AddFile("skills/.gitignore", "*.md\n!keep.md\n");
        env.AddFile("skills/.ignore", "ignore-dir/\n");
        env.AddFile("skills/.fdignore", "fd-dir/\n");
        env.AddFile("skills/drop.md", "---\ndescription: Drop\n---\nDrop");
        env.AddFile("skills/keep.md", "---\ndescription: Keep\n---\nKeep");
        env.AddFile(
            "skills/nested/SKILL.md",
            "---\nname: nested\ndescription: Nested\n---\nNested");
        env.AddFile(
            "skills/ignore-dir/SKILL.md",
            "---\nname: ignore-dir\ndescription: Ignored\n---\nIgnored");
        env.AddFile(
            "skills/fd-dir/SKILL.md",
            "---\nname: fd-dir\ndescription: Ignored\n---\nIgnored");

        var result = await SkillLoader.LoadSkillsAsync(env, "skills");

        var skill = Assert.Single(result.Skills);
        Assert.Equal("skills", skill.Name);
        Assert.Equal("Keep", skill.Content);
        Assert.Equal(env.Absolute("skills/keep.md"), skill.FilePath);
        Assert.Empty(result.Diagnostics);
    }

    [Fact(DisplayName = "matches gitignore negation and directory-only rules")]
    public void Matches_gitignore_negation_and_directory_only_rules()
    {
        var matcher = new GitIgnoreMatcher();
        matcher.Add(["build/", "*.tmp", "!keep.tmp"]);

        Assert.True(matcher.Ignores("build/"));
        Assert.True(matcher.Ignores("build/output.txt"));
        Assert.True(matcher.Ignores("lost.tmp"));
        Assert.False(matcher.Ignores("keep.tmp"));
    }

    [Fact(DisplayName = "preserves multiline descriptions and reports malformed declared frontmatter")]
    public async Task Preserves_multiline_descriptions_and_reports_malformed_declared_frontmatter()
    {
        var env = new SkillTestExecutionEnv();
        env.AddDirectory("skills/multiline");
        env.AddDirectory("skills/broken");
        env.AddFile(
            "skills/multiline/SKILL.md",
            "---\nname: multiline\ndescription: |\n  This is a multiline description.\n  It spans multiple lines.\n---\nBody");
        env.AddFile("skills/broken/SKILL.md", "---\nname: broken\ndescription: [unclosed\n---\nBody");

        var result = await SkillLoader.LoadSkillsAsync(env, "skills");

        var skill = Assert.Single(result.Skills);
        Assert.Equal("multiline", skill.Name);
        Assert.Contains("This is a multiline description.", skill.Description, StringComparison.Ordinal);
        Assert.Contains("It spans multiple lines.", skill.Description, StringComparison.Ordinal);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SkillDiagnosticCodes.ParseFailed, diagnostic.Code);
        Assert.Equal(env.Absolute("skills/broken/SKILL.md"), diagnostic.Path);
    }
}
