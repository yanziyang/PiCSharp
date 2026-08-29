namespace Pi.Telemetry;

/// <summary>
/// Backend-neutral telemetry context that records spans in process memory.
/// Create a fresh instance to isolate tests or independent recording scopes.
/// </summary>
public sealed class InMemoryTelemetryContext : TelemetryContext
{
    private readonly State _state = new();

    /// <inheritdoc />
    public Task<T> StartSpan<T>(
        SpanOptions options,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ArgumentNullException.ThrowIfNull(callback);

        if (!TryCreateSpan(options, parent: null, out var recordedSpan) || recordedSpan is null)
        {
            return NoopTelemetryContext.Instance.StartSpan(options, callback, cancellationToken);
        }

        var span = new InMemoryTelemetrySpan(this, recordedSpan);
        try
        {
            var result = callback(span);
            Settle(recordedSpan, failed: false, error: null);
            return Task.FromResult(result);
        }
        catch (Exception error)
        {
            Settle(recordedSpan, failed: true, error);
            return Task.FromException<T>(error);
        }
    }

    /// <inheritdoc />
    public Task<T> StartSpanAsync<T>(
        SpanOptions options,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ArgumentNullException.ThrowIfNull(callback);

        if (!TryCreateSpan(options, parent: null, out var recordedSpan) || recordedSpan is null)
        {
            return NoopTelemetryContext.Instance.StartSpanAsync(options, callback, cancellationToken);
        }

        var span = new InMemoryTelemetrySpan(this, recordedSpan);
        try
        {
            var result = callback(span);
            return SettleAsync(result, recordedSpan);
        }
        catch (Exception error)
        {
            Settle(recordedSpan, failed: true, error);
            return Task.FromException<T>(error);
        }
    }

    /// <summary>Returns detached snapshots in span-start order.</summary>
    public IReadOnlyList<RecordedTelemetrySpan> GetSpans()
    {
        var snapshots = new List<RecordedTelemetrySpan>(_state.Spans.Count);
        foreach (var span in _state.Spans)
        {
            snapshots.Add(new RecordedTelemetrySpan(
                span.Id,
                span.ParentId,
                span.Name,
                CopyAttributes(span.Attributes),
                span.Events
                    .Select(static recordedEvent => new RecordedTelemetryEvent(
                        recordedEvent.Name,
                        CopyAttributes(recordedEvent.Attributes)))
                    .ToArray(),
                CopyStatus(span.Status),
                span.Settled,
                span.EndSequence));
        }

        return snapshots;
    }

    private Task<T> StartChild<T>(
        MutableRecordedTelemetrySpan parent,
        SpanOptions options,
        Func<TelemetrySpan, T> callback,
        CancellationToken cancellationToken)
    {
        if (parent.Settled)
        {
            return NoopTelemetryContext.Instance.StartSpan(options, callback, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(callback);
        if (!TryCreateSpan(options, parent, out var recordedSpan) || recordedSpan is null)
        {
            return NoopTelemetryContext.Instance.StartSpan(options, callback, cancellationToken);
        }

        var span = new InMemoryTelemetrySpan(this, recordedSpan);
        try
        {
            var result = callback(span);
            Settle(recordedSpan, failed: false, error: null);
            return Task.FromResult(result);
        }
        catch (Exception error)
        {
            Settle(recordedSpan, failed: true, error);
            return Task.FromException<T>(error);
        }
    }

    private Task<T> StartChildAsync<T>(
        MutableRecordedTelemetrySpan parent,
        SpanOptions options,
        Func<TelemetrySpan, Task<T>> callback,
        CancellationToken cancellationToken)
    {
        if (parent.Settled)
        {
            return NoopTelemetryContext.Instance.StartSpanAsync(options, callback, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(callback);
        if (!TryCreateSpan(options, parent, out var recordedSpan) || recordedSpan is null)
        {
            return NoopTelemetryContext.Instance.StartSpanAsync(options, callback, cancellationToken);
        }

        var span = new InMemoryTelemetrySpan(this, recordedSpan);
        try
        {
            var result = callback(span);
            return SettleAsync(result, recordedSpan);
        }
        catch (Exception error)
        {
            Settle(recordedSpan, failed: true, error);
            return Task.FromException<T>(error);
        }
    }

    private async Task<T> SettleAsync<T>(Task<T> result, MutableRecordedTelemetrySpan span)
    {
        try
        {
            var value = await result.ConfigureAwait(false);
            Settle(span, failed: false, error: null);
            return value;
        }
        catch (Exception error)
        {
            Settle(span, failed: true, error);
            throw;
        }
    }

    private bool TryCreateSpan(
        SpanOptions options,
        MutableRecordedTelemetrySpan? parent,
        out MutableRecordedTelemetrySpan? recordedSpan)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            if (parent?.Settled == true)
            {
                recordedSpan = null;
                return false;
            }

            var name = options.Name;
            var attributes = CopyAttributes(options.Attributes);
            recordedSpan = new MutableRecordedTelemetrySpan
            {
                Id = _state.NextSpanId++,
                ParentId = parent?.Id,
                Name = name,
                Attributes = attributes,
                Status = new SpanStatus.Ok(),
            };
            _state.Spans.Add(recordedSpan);
            return true;
        }
        catch
        {
            recordedSpan = null;
            return false;
        }
    }

    private void Settle(MutableRecordedTelemetrySpan span, bool failed, Exception? error)
    {
        if (span.Settled)
        {
            return;
        }

        if (failed && !span.ExplicitStatus)
        {
            span.Status = AutomaticErrorStatus(error);
        }

        span.Settled = true;
        span.EndSequence = _state.NextEndSequence++;
    }

    private static SpanStatus.Error AutomaticErrorStatus(Exception? error) =>
        error is null
            ? new SpanStatus.Error()
            : new SpanStatus.Error(new SpanError(error.GetType().Name, error.Message));

    private static Dictionary<string, object?> CopyAttributes(
        IReadOnlyDictionary<string, object?>? attributes)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (attributes is null)
        {
            return copy;
        }

        foreach (var pair in attributes)
        {
            if (pair.Value is not null)
            {
                copy[pair.Key] = CopyAttributeValue(pair.Value);
            }
        }

        return copy;
    }

    private static Dictionary<string, object?> MergeAttributes(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyDictionary<string, object?> attributes)
    {
        var merged = CopyAttributes(current);
        foreach (var pair in attributes)
        {
            if (pair.Value is not null)
            {
                merged[pair.Key] = CopyAttributeValue(pair.Value);
            }
        }

        return merged;
    }

    private static object CopyAttributeValue(object value) =>
        value is Array array ? array.Clone() : value;

    private static SpanStatus CopyStatus(SpanStatus status) => status switch
    {
        SpanStatus.Ok => new SpanStatus.Ok(),
        SpanStatus.Error { Details: null } => new SpanStatus.Error(),
        SpanStatus.Error { Details: var details } =>
            new SpanStatus.Error(new SpanError(details.Name, details.Message)),
        _ => new SpanStatus.Error(),
    };

    private sealed class State
    {
        public List<MutableRecordedTelemetrySpan> Spans { get; } = [];

        public int NextSpanId { get; set; } = 1;

        public int NextEndSequence { get; set; } = 1;
    }

    private sealed class MutableRecordedTelemetrySpan
    {
        public int Id { get; init; }

        public int? ParentId { get; init; }

        public string Name { get; init; } = string.Empty;

        public Dictionary<string, object?> Attributes { get; set; } = new(StringComparer.Ordinal);

        public List<MutableRecordedTelemetryEvent> Events { get; } = [];

        public SpanStatus Status { get; set; } = new SpanStatus.Ok();

        public bool ExplicitStatus { get; set; }

        public bool Settled { get; set; }

        public int? EndSequence { get; set; }
    }

    private sealed class MutableRecordedTelemetryEvent
    {
        public string Name { get; init; } = string.Empty;

        public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class InMemoryTelemetrySpan(
        InMemoryTelemetryContext owner,
        MutableRecordedTelemetrySpan recordedSpan) : TelemetrySpan
    {
        public Task<T> StartSpan<T>(
            SpanOptions options,
            Func<TelemetrySpan, T> callback,
            CancellationToken cancellationToken = default) =>
            owner.StartChild(recordedSpan, options, callback, cancellationToken);

        public Task<T> StartSpanAsync<T>(
            SpanOptions options,
            Func<TelemetrySpan, Task<T>> callback,
            CancellationToken cancellationToken = default) =>
            owner.StartChildAsync(recordedSpan, options, callback, cancellationToken);

        public void AddEvent(string name, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            if (recordedSpan.Settled)
            {
                return;
            }

            try
            {
                recordedSpan.Events.Add(new MutableRecordedTelemetryEvent
                {
                    Name = name,
                    Attributes = CopyAttributes(attributes),
                });
            }
            catch
            {
                // Recording is passive. Malformed or unreadable telemetry is ignored.
            }
        }

        public void SetAttributes(IReadOnlyDictionary<string, object?> attributes)
        {
            if (recordedSpan.Settled)
            {
                return;
            }

            try
            {
                recordedSpan.Attributes = MergeAttributes(recordedSpan.Attributes, attributes);
            }
            catch
            {
                // Recording is passive and the merge is atomic on failure.
            }
        }

        public void SetStatus(SpanStatus status)
        {
            if (recordedSpan.Settled)
            {
                return;
            }

            try
            {
                recordedSpan.Status = CopyStatus(status);
                recordedSpan.ExplicitStatus = true;
            }
            catch
            {
                // Recording is passive and status updates are atomic on failure.
            }
        }
    }
}
