using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Pi.AgentCore.Harness;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Serializes mutations targeting the same execution environment and canonical path.</summary>
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "The name is the public counterpart of the upstream file-mutation-queue utility.")]
public static class FileMutationQueue
{
    private static readonly ConditionalWeakTable<ExecutionEnv, MutationQueueState> _states = new();

    /// <summary>Runs a file mutation after earlier mutations for the same path settle.</summary>
    public static async Task<T> WithFileMutationQueueAsync<T>(
        ExecutionEnv env,
        string path,
        Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(action);
        var state = _states.GetValue(env, static _ => new MutationQueueState());
        Task<Registration> registration;
        lock (state.Gate)
        {
            registration = state.Registration.ContinueWith(
                    _ => RegisterAsync(state, env, path),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            state.Registration = registration.ContinueWith(
                    static _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        var item = await registration.ConfigureAwait(false);
        await item.CurrentQueue.ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            item.ReleaseNext();
            lock (state.Gate)
            {
                if (state.Queues.TryGetValue(item.Key, out var current) && ReferenceEquals(current, item.ChainedQueue))
                {
                    state.Queues.Remove(item.Key);
                }
            }
        }
    }

    private static async Task<Registration> RegisterAsync(
        MutationQueueState state,
        ExecutionEnv env,
        string path)
    {
        var key = await GetMutationQueueKeyAsync(env, path).ConfigureAwait(false);
        TaskCompletionSource<object?> releaseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (state.Gate)
        {
            var currentQueue = state.Queues.GetValueOrDefault(key) ?? Task.CompletedTask;
            var chainedQueue = currentQueue.ContinueWith(
                    _ => releaseSource.Task,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            state.Queues[key] = chainedQueue;
            return new Registration(key, currentQueue, chainedQueue, () => releaseSource.TrySetResult(null));
        }
    }

    private static async Task<string> GetMutationQueueKeyAsync(ExecutionEnv env, string path)
    {
        var absolutePath = Result.GetOrThrow(await env.AbsolutePathAsync(path).ConfigureAwait(false));
        var canonicalPath = await env.CanonicalPathAsync(absolutePath).ConfigureAwait(false);
        if (canonicalPath.Ok)
        {
            return canonicalPath.Value!;
        }

        if (canonicalPath.Error?.Code is FileErrorCodes.NotFound or FileErrorCodes.NotSupported)
        {
            return absolutePath;
        }

        throw canonicalPath.Error!;
    }

    private sealed class MutationQueueState
    {
        public object Gate { get; } = new();
        public Dictionary<string, Task> Queues { get; } = new(StringComparer.Ordinal);
        public Task Registration { get; set; } = Task.CompletedTask;
    }

    private sealed record Registration(
        string Key,
        Task CurrentQueue,
        Task ChainedQueue,
        Action ReleaseNext);
}
