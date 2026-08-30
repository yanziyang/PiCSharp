using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Factory for the built-in write tool.</summary>
public static class WriteTool
{
    /// <summary>Creates a tool that writes UTF-8 text and creates parent directories.</summary>
    public static AgentHarnessTool<ExecutionToolContext> CreateWriteTool() => CreateWriteTool<ExecutionToolContext>();

    /// <summary>Creates a write tool for a derived execution context.</summary>
    public static AgentHarnessTool<TContext> CreateWriteTool<TContext>()
        where TContext : ExecutionToolContext => new()
        {
            Name = "write",
            Label = "write",
            Description = "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.",
            Parameters = ToolHelpers.Schema(
                ("path", "string", "Path to the file to write (relative or absolute)", true),
                ("content", "string", "Content to write to the file", true)),
            Execute = async (toolCallId, parameters, signal, onUpdate, context) =>
            {
                _ = toolCallId;
                _ = onUpdate;
                ArgumentNullException.ThrowIfNull(context);
                var input = ToolHelpers.RequireObject(parameters);
                var path = ToolHelpers.RequireString(input, "path");
                var content = ToolHelpers.RequireString(input, "content");
                var absolutePath = await ToolPathUtilities.ResolveToolPathAsync(context.Env, path, signal).ConfigureAwait(false);
                return await FileMutationQueue.WithFileMutationQueueAsync(context.Env, absolutePath, async () =>
                {
                    ToolHelpers.ThrowIfAborted(signal);
                    Result.GetOrThrow(await context.Env.WriteFileAsync(absolutePath, content, signal).ConfigureAwait(false));
                    ToolHelpers.ThrowIfAborted(signal);
                    return ToolHelpers.TextResult($"Successfully wrote {content.Length} bytes to {path}");
                }).ConfigureAwait(false);
            },
        };
}
