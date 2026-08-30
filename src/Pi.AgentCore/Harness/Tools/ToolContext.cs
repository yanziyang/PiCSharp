namespace Pi.AgentCore.Harness.Tools;

/// <summary>Filesystem and shell context required by the built-in execution tools.</summary>
public class ExecutionToolContext
{
    /// <summary>Environment used for file and process operations.</summary>
    public required ExecutionEnv Env { get; init; }
}
