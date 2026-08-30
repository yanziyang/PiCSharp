using Pi.AgentCore.Harness;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Convenience access to the built-in harness tools.</summary>
public static class HarnessTools
{
    /// <summary>Creates the bash tool.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateBashTool(
        BashToolOptions<ExecutionToolContext>? options = null) => BashTool.CreateBashTool(options);

    /// <summary>Creates the edit tool.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateEditTool() => EditTool.CreateEditTool();

    /// <summary>Creates the read tool.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateReadTool(
        ReadToolOptions? options = null) => ReadTool.CreateReadTool(options);

    /// <summary>Creates the write tool.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateWriteTool() => WriteTool.CreateWriteTool();
}
