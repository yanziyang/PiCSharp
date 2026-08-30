using Pi.AgentCore.Harness.Tools;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

/// <summary>Golden fixtures for the hand-written formatter that replaces npm diff's patch writer.</summary>
public sealed class EditDiffGoldenTests
{
    [Fact(DisplayName = "matches the TypeScript unified patch golden for no change")]
    public void Matches_the_typescript_unified_patch_golden_for_no_change()
    {
        Assert.Equal(
            "===================================================================\n" +
            "--- fixture.txt\n" +
            "+++ fixture.txt\n",
            EditDiff.GenerateUnifiedPatch("fixture.txt", "one\n", "one\n"));
    }

    [Fact(DisplayName = "matches the TypeScript unified patch golden for pure insertion")]
    public void Matches_the_typescript_unified_patch_golden_for_pure_insertion()
    {
        Assert.Equal(
            "===================================================================\n" +
            "--- fixture.txt\n" +
            "+++ fixture.txt\n" +
            "@@ -1,2 +1,3 @@\n" +
            " one\n" +
            " two\n" +
            "+three\n",
            EditDiff.GenerateUnifiedPatch("fixture.txt", "one\ntwo\n", "one\ntwo\nthree\n"));
    }

    [Fact(DisplayName = "matches the TypeScript unified patch golden for pure deletion")]
    public void Matches_the_typescript_unified_patch_golden_for_pure_deletion()
    {
        Assert.Equal(
            "===================================================================\n" +
            "--- fixture.txt\n" +
            "+++ fixture.txt\n" +
            "@@ -1,3 +1,2 @@\n" +
            " one\n" +
            "-two\n" +
            " three\n",
            EditDiff.GenerateUnifiedPatch("fixture.txt", "one\ntwo\nthree\n", "one\nthree\n"));
    }

    [Fact(DisplayName = "matches the TypeScript unified patch golden for adjacent hunks merged at context four")]
    public void Matches_the_typescript_unified_patch_golden_for_adjacent_hunks_merged_at_context_four()
    {
        var oldContent = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"line-{index}")) + '\n';
        var newContent = oldContent.Replace("line-2", "LINE-2", StringComparison.Ordinal)
            .Replace("line-9", "LINE-9", StringComparison.Ordinal);
        var expected =
            "===================================================================\n" +
            "--- fixture.txt\n" +
            "+++ fixture.txt\n" +
            "@@ -1,12 +1,12 @@\n" +
            " line-1\n" +
            "-line-2\n" +
            "+LINE-2\n" +
            " line-3\n" +
            " line-4\n" +
            " line-5\n" +
            " line-6\n" +
            " line-7\n" +
            " line-8\n" +
            "-line-9\n" +
            "+LINE-9\n" +
            " line-10\n" +
            " line-11\n" +
            " line-12\n";

        Assert.Equal(expected, EditDiff.GenerateUnifiedPatch("fixture.txt", oldContent, newContent));
    }

    [Fact(DisplayName = "matches the TypeScript unified patch golden without a trailing newline")]
    public void Matches_the_typescript_unified_patch_golden_without_a_trailing_newline()
    {
        Assert.Equal(
            "===================================================================\n" +
            "--- fixture.txt\n" +
            "+++ fixture.txt\n" +
            "@@ -1,2 +1,2 @@\n" +
            " one\n" +
            "-two\n" +
            "\\ No newline at end of file\n" +
            "+TWO\n" +
            "\\ No newline at end of file\n",
            EditDiff.GenerateUnifiedPatch("fixture.txt", "one\ntwo", "one\nTWO"));
    }
}
