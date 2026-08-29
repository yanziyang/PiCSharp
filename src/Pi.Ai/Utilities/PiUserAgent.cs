using System.Runtime.InteropServices;

namespace Pi.Ai;

/// <summary>Builds the Pi user-agent string for the current host.</summary>
public static class PiUserAgent
{
    /// <summary>Returns a Pi user-agent containing the host platform, release, and architecture.</summary>
    public static string GetPiUserAgent()
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win32"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "linux"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "darwin"
                    : RuntimeInformation.OSDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var release = Environment.OSVersion.VersionString;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return $"pi ({platform} {release}; {architecture})";
    }
}
