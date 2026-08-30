using Pi.AgentCore.Harness;
using Pi.Ai;

namespace Pi.AgentCore.Tests.Harness;

internal static class H4TestSupport
{
    public static TempDirectory CreateTempDirectory(string prefix = "pi-h4-") => new(prefix);

    public static string TextOutput(AgentToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContent>().Select(static content => content.Text));

    public static bool TryCreateSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(targetPath))
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static async Task DelayAsync(int milliseconds) => await Task.Delay(milliseconds).ConfigureAwait(false);

    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{prefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // The execution environment's cleanup is intentionally best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // The execution environment's cleanup is intentionally best effort.
            }
        }
    }
}
