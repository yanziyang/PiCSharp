using System.Collections;
using System.Text.Json.Nodes;

using Pi.Ai;

namespace Pi.AgentCore.Harness.Session;

/// <summary>Session view and lifecycle operations.</summary>
public sealed class Session<TMetadata>
    where TMetadata : SessionMetadata
{
    private readonly ISessionStorage<TMetadata> _storage;

    /// <summary>Identifier generator shared by all lane views.</summary>
    public IIdGenerator IdGenerator { get; }

    /// <summary>Creates a session over a storage implementation.</summary>
    public Session(ISessionStorage<TMetadata> storage, IIdGenerator? idGenerator = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        IdGenerator = idGenerator ?? new GuidIdGenerator();
    }

    /// <summary>Gets session metadata.</summary>
    public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        _storage.GetMetadataAsync(cancellationToken);

    /// <summary>Returns a lane-bound view. The view resolves its leaf at call time.</summary>
    public SessionTree<TMetadata> View(string lane) =>
        new(this, lane ?? throw new ArgumentNullException(nameof(lane)));

    /// <summary>Gets the main lane leaf.</summary>
    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => GetLeafIdForLaneAsync("main", cancellationToken);

    /// <summary>Gets an entry by identifier.</summary>
    public Task<Entry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetEntryAsync(id, cancellationToken);

    /// <summary>Gets accumulated statistics.</summary>
    public Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        _storage.GetStatsAsync(cancellationToken);

    /// <summary>Gets the current session name.</summary>
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) =>
        _storage.GetNameAsync(cancellationToken);

    /// <summary>Sets or clears the session name.</summary>
    public Task SetNameAsync(string? name, CancellationToken cancellationToken = default) =>
        _storage.SetNameAsync(name, cancellationToken);

    /// <summary>Gets an entry label.</summary>
    public Task<string?> GetLabelAsync(string targetId, CancellationToken cancellationToken = default) =>
        _storage.GetLabelAsync(targetId, cancellationToken);

    /// <summary>Sets or clears an entry label.</summary>
    public Task SetLabelAsync(string targetId, string? label, CancellationToken cancellationToken = default) =>
        _storage.SetLabelAsync(targetId, label, cancellationToken);

    /// <summary>Finds entries across all branches.</summary>
    public Task<IReadOnlyList<Entry>> FindEntriesAsync(
        EntryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var actual = query ?? new EntryQuery();
        SessionState.ValidateQuery(actual.Limit, actual.Cursor?.AfterSeq);
        return _storage.FindEntriesAsync(actual, cancellationToken);
    }

    /// <summary>Finds the newest matching entry across all branches.</summary>
    public async Task<Entry?> FindEntryAsync(EntryQuery? query = null, CancellationToken cancellationToken = default)
    {
        var actual = query ?? new EntryQuery();
        SessionState.ValidateQuery(actual.Limit, actual.Cursor?.AfterSeq);
        var result = await _storage.FindEntriesAsync(actual with { Limit = 1 }, cancellationToken).ConfigureAwait(false);
        return result.Count == 0 ? null : result[0];
    }

    /// <summary>Finds entries on the main branch, leaf toward root by default.</summary>
    public async Task<IReadOnlyList<Entry>> FindEntriesOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default)
    {
        var actual = query ?? new EntryQuery();
        var actualBounds = bounds ?? new BranchBounds();
        SessionState.ValidateQuery(actual.Limit, actual.Cursor?.AfterSeq);
        var start = actualBounds.Start ?? await GetLeafIdForLaneAsync("main", cancellationToken).ConfigureAwait(false);
        return start is null
            ? []
            : await _storage.FindEntriesOnBranchAsync(actual, start, actualBounds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds the newest matching entry on the main branch.</summary>
    public async Task<Entry?> FindEntryOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default)
    {
        var actual = query ?? new EntryQuery();
        var actualBounds = bounds ?? new BranchBounds();
        SessionState.ValidateQuery(actual.Limit, actual.Cursor?.AfterSeq);
        var start = actualBounds.Start ?? await GetLeafIdForLaneAsync("main", cancellationToken).ConfigureAwait(false);
        if (start is null)
        {
            return null;
        }

        var result = await _storage.FindEntriesOnBranchAsync(
            actual with { Limit = 1 },
            start,
            actualBounds,
            cancellationToken).ConfigureAwait(false);
        return result.Count == 0 ? null : result[0];
    }

    /// <summary>Gets all records matching a query.</summary>
    public Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(
        RecordQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var actual = query ?? new RecordQuery();
        ValidateRecordQuery(actual);
        return _storage.FindRecordsAsync(actual, cancellationToken);
    }

    /// <summary>Gets unfinished operations on a lane, newest first.</summary>
    public Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        SessionState.ValidateQuery(limit, null);
        return _storage.FindOpenOperationsAsync(lane, limit, cancellationToken);
    }

    /// <summary>Gets the append-only log.</summary>
    public Task<IReadOnlyList<LogItem>> GetLogAsync(
        LogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var actual = options ?? new LogOptions();
        SessionState.ValidateQuery(actual.Limit, actual.AfterSeq);
        return _storage.GetLogAsync(actual, cancellationToken);
    }

    /// <summary>Gets lane pointers in insertion order.</summary>
    public Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLanesAsync(cancellationToken);

    /// <summary>Creates a lane from an existing entry or from an empty state.</summary>
    public Task CreateLaneAsync(string lane, string? at, CancellationToken cancellationToken = default) =>
        _storage.CreateLaneAsync(lane, at, cancellationToken);

    /// <summary>Moves an existing lane to an entry or to an empty state.</summary>
    public Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default) =>
        _storage.MoveLaneAsync(lane, to, cancellationToken);

    /// <summary>Appends a storage-assigned entry to a lane.</summary>
    public async Task<TEntry> AppendEntryAsync<TEntry>(
        TEntry entry,
        string lane = "main",
        CancellationToken cancellationToken = default)
        where TEntry : Entry
    {
        ArgumentNullException.ThrowIfNull(entry);
        AssertJsonSerializable(SessionJson.EntryToJson(entry, includeStorageFields: false));
        return (TEntry)await _storage.AppendEntryAsync(entry, lane, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends a record.</summary>
    public async Task<TRecord> AppendRecordAsync<TRecord>(
        TRecord record,
        CancellationToken cancellationToken = default)
        where TRecord : LaneRecord
    {
        ArgumentNullException.ThrowIfNull(record);
        AssertJsonSerializable(SessionJson.RecordToJson(record));
        return (TRecord)await _storage.AppendRecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends a message with a generated identifier.</summary>
    public Task<string> AppendMessageAsync(Message message, CancellationToken cancellationToken = default) =>
        AppendMessageAsync(new AgentMessage(message), cancellationToken);

    /// <summary>Appends an extensible agent message with a generated identifier.</summary>
    public async Task<string> AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = await AppendEntryAsync(
            new MessageEntry { Id = IdGenerator.Next(), Message = message },
            "main",
            cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }

    /// <summary>Appends an extensible agent message to a lane.</summary>
    internal async Task<string> AppendMessageToLaneAsync(
        string lane,
        AgentMessage message,
        CancellationToken cancellationToken = default)
    {
        var entry = await AppendEntryAsync(
            new MessageEntry { Id = IdGenerator.Next(), Message = message },
            lane,
            cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }

    /// <summary>Appends a custom entry without data.</summary>
    public Task<string> AppendCustomEntryAsync(string customType, CancellationToken cancellationToken = default) =>
        AppendCustomEntryToLaneAsync("main", customType, data: null, dataProvided: false, cancellationToken: cancellationToken);

    /// <summary>Appends a custom entry with a JSON-compatible data value.</summary>
    public Task<string> AppendCustomEntryAsync(
        string customType,
        object? data,
        CancellationToken cancellationToken = default) =>
        AppendCustomEntryToLaneAsync("main", customType, data, dataProvided: true, cancellationToken: cancellationToken);

    internal Task<string> AppendCustomEntryToLaneAsync(
        string lane,
        string customType,
        object? data,
        bool dataProvided,
        CancellationToken cancellationToken = default)
    {
        var node = dataProvided ? SessionDurability.ToJsonNode(data) : null;
        var entry = new CustomEntry
        {
            Id = IdGenerator.Next(),
            CustomType = customType,
            Data = node,
            DataPresent = dataProvided,
        };
        return AppendCustomEntryNodeToLaneAsync(lane, entry, cancellationToken);
    }

    private async Task<string> AppendCustomEntryNodeToLaneAsync(
        string lane,
        CustomEntry entry,
        CancellationToken cancellationToken)
    {
        var appended = await AppendEntryAsync(entry, lane, cancellationToken).ConfigureAwait(false);
        return appended.Id;
    }

    private async Task<string?> GetLeafIdForLaneAsync(string lane, CancellationToken cancellationToken)
    {
        var lanes = await _storage.GetLanesAsync(cancellationToken).ConfigureAwait(false);
        var pointer = lanes.FirstOrDefault(candidate => candidate.Lane == lane);
        if (pointer is null)
        {
            throw new SessionError(SessionErrorCode.InvalidLane, $"Lane not found: {lane}");
        }

        return pointer.LeafId;
    }

    internal Task<IReadOnlyList<Entry>> FindEntriesOnBranchInternalAsync(
        EntryQuery query,
        string start,
        BranchBounds bounds,
        CancellationToken cancellationToken) =>
        _storage.FindEntriesOnBranchAsync(query, start, bounds, cancellationToken);

    private static void ValidateRecordQuery(RecordQuery query)
    {
        SessionState.ValidateQuery(query.Limit, query.AfterSeq);
        if (query.OperationKind is not null && query.Type != "operation_started")
        {
            throw new SessionError(SessionErrorCode.InvalidQuery, "operationKind requires type \"operation_started\"");
        }
    }

    internal static void AssertJsonSerializable(JsonNode? value)
    {
        try
        {
            SessionDurability.Validate(value);
        }
        catch (SessionError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SessionError(SessionErrorCode.InvalidPayload, $"Durable payload contains {error.Message}", error);
        }
    }

    private sealed class GuidIdGenerator : IIdGenerator
    {
        public string Next() => Guid.NewGuid().ToString("N");
    }
}

/// <summary>Lane-bound session view.</summary>
public sealed class SessionTree<TMetadata>
    where TMetadata : SessionMetadata
{
    private readonly Session<TMetadata> _session;
    private readonly string _lane;

    internal SessionTree(Session<TMetadata> session, string lane)
    {
        _session = session;
        _lane = lane;
    }

    /// <summary>Gets this view's current leaf.</summary>
    public async Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default)
    {
        var lanes = await _session.GetLanesAsync(cancellationToken).ConfigureAwait(false);
        var pointer = lanes.FirstOrDefault(candidate => candidate.Lane == _lane);
        if (pointer is null)
        {
            throw new SessionError(SessionErrorCode.InvalidLane, $"Lane not found: {_lane}");
        }

        return pointer.LeafId;
    }

    /// <summary>Gets an entry.</summary>
    public Task<Entry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) =>
        _session.GetEntryAsync(id, cancellationToken);

    /// <summary>Gets session statistics.</summary>
    public Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        _session.GetStatsAsync(cancellationToken);

    /// <summary>Gets the session name.</summary>
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => _session.GetNameAsync(cancellationToken);

    /// <summary>Sets the session name.</summary>
    public Task SetNameAsync(string? name, CancellationToken cancellationToken = default) => _session.SetNameAsync(name, cancellationToken);

    /// <summary>Gets an entry label.</summary>
    public Task<string?> GetLabelAsync(string targetId, CancellationToken cancellationToken = default) => _session.GetLabelAsync(targetId, cancellationToken);

    /// <summary>Sets an entry label.</summary>
    public Task SetLabelAsync(string targetId, string? label, CancellationToken cancellationToken = default) => _session.SetLabelAsync(targetId, label, cancellationToken);

    /// <summary>Finds entries across all branches.</summary>
    public Task<IReadOnlyList<Entry>> FindEntriesAsync(EntryQuery? query = null, CancellationToken cancellationToken = default) =>
        _session.FindEntriesAsync(query, cancellationToken);

    /// <summary>Finds one entry across all branches.</summary>
    public Task<Entry?> FindEntryAsync(EntryQuery? query = null, CancellationToken cancellationToken = default) =>
        _session.FindEntryAsync(query, cancellationToken);

    /// <summary>Finds entries on this lane's branch.</summary>
    public Task<IReadOnlyList<Entry>> FindEntriesOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default)
    {
        var actual = bounds ?? new BranchBounds();
        return _session.FindEntriesOnBranchForLaneAsync(_lane, query, actual, cancellationToken);
    }

    /// <summary>Finds one entry on this lane's branch.</summary>
    public Task<Entry?> FindEntryOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default) =>
        _session.FindEntryOnBranchForLaneAsync(_lane, query, bounds ?? new BranchBounds(), cancellationToken);

    /// <summary>Appends a message to this lane.</summary>
    public Task<string> AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default) =>
        _session.AppendMessageToLaneAsync(_lane, message, cancellationToken);

    /// <summary>Appends a shared Pi AI message to this lane.</summary>
    public Task<string> AppendMessageAsync(Message message, CancellationToken cancellationToken = default) =>
        AppendMessageAsync(new AgentMessage(message), cancellationToken);

    /// <summary>Appends a custom entry without data to this lane.</summary>
    public Task<string> AppendCustomEntryAsync(string customType, CancellationToken cancellationToken = default) =>
        _session.AppendCustomEntryToLaneAsync(_lane, customType, data: null, dataProvided: false, cancellationToken: cancellationToken);

    /// <summary>Appends a custom entry with data to this lane.</summary>
    public Task<string> AppendCustomEntryAsync(string customType, object? data, CancellationToken cancellationToken = default) =>
        _session.AppendCustomEntryToLaneAsync(_lane, customType, data, dataProvided: true, cancellationToken: cancellationToken);
}

internal static class SessionTreeExtensions
{
    internal static async Task<IReadOnlyList<Entry>> FindEntriesOnBranchForLaneAsync<TMetadata>(
        this Session<TMetadata> session,
        string lane,
        EntryQuery? query,
        BranchBounds bounds,
        CancellationToken cancellationToken)
        where TMetadata : SessionMetadata
    {
        var actual = query ?? new EntryQuery();
        SessionState.ValidateQuery(actual.Limit, actual.Cursor?.AfterSeq);
        var lanes = await session.GetLanesAsync(cancellationToken).ConfigureAwait(false);
        var pointer = lanes.FirstOrDefault(candidate => candidate.Lane == lane);
        if (pointer is null)
        {
            throw new SessionError(SessionErrorCode.InvalidLane, $"Lane not found: {lane}");
        }

        var start = bounds.Start ?? pointer.LeafId;
        if (start is null)
        {
            return [];
        }

        // This method is implemented by the internal storage bridge below.
        return await session.FindEntriesOnBranchFromStorageAsync(actual, start, bounds, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Entry?> FindEntryOnBranchForLaneAsync<TMetadata>(
        this Session<TMetadata> session,
        string lane,
        EntryQuery? query,
        BranchBounds bounds,
        CancellationToken cancellationToken)
        where TMetadata : SessionMetadata
    {
        var result = await session.FindEntriesOnBranchForLaneAsync(lane, query, bounds, cancellationToken).ConfigureAwait(false);
        return result.Count == 0 ? null : result[0];
    }

    internal static Task<IReadOnlyList<Entry>> FindEntriesOnBranchFromStorageAsync<TMetadata>(
        this Session<TMetadata> session,
        EntryQuery query,
        string start,
        BranchBounds bounds,
        CancellationToken cancellationToken)
        where TMetadata : SessionMetadata
    {
        return session.FindEntriesOnBranchInternalAsync(query, start, bounds, cancellationToken);
    }
}

internal static class SessionDurability
{
    internal static JsonNode? ToJsonNode(object? value)
    {
        try
        {
            if (value is null)
            {
                return null;
            }

            if (value is JsonNode node)
            {
                var clone = node.DeepClone();
                Validate(clone);
                return clone;
            }

            if (value is AgentMessage message)
            {
                var clone = message.Value.DeepClone();
                Validate(clone);
                return clone;
            }

            if (value is Message messageValue)
            {
                var clone = SessionJson.MessageToJson(messageValue);
                Validate(clone);
                return clone;
            }

            var serialized = ConvertStructuredValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
            Validate(serialized);
            return serialized;
        }
        catch (SessionError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SessionError(SessionErrorCode.InvalidPayload, $"Durable payload contains {error.Message}", error);
        }
    }

    internal static void Validate(JsonNode? value)
    {
        var active = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        Visit(value, active);
    }

    private static void Visit(JsonNode? value, HashSet<JsonNode> active)
    {
        if (value is null)
        {
            return;
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<double>(out var number) && !double.IsFinite(number))
            {
                throw new SessionError(SessionErrorCode.InvalidPayload, "Durable payload contains a non-finite number");
            }

            return;
        }

        if (!active.Add(value))
        {
            throw new SessionError(SessionErrorCode.InvalidPayload, "Durable payload contains a cycle");
        }

        if (value is JsonArray array)
        {
            foreach (var item in array)
            {
                Visit(item, active);
            }
        }
        else if (value is JsonObject objectValue)
        {
            foreach (var item in objectValue)
            {
                Visit(item.Value, active);
            }
        }

        active.Remove(value);
    }

    private static JsonNode? ConvertStructuredValue(object value, HashSet<object> active)
    {
        switch (value)
        {
            case string text:
                return JsonValue.Create(text);
            case bool boolean:
                return JsonValue.Create(boolean);
            case byte number:
                return JsonValue.Create(number);
            case sbyte number:
                return JsonValue.Create(number);
            case short number:
                return JsonValue.Create(number);
            case ushort number:
                return JsonValue.Create(number);
            case int number:
                return JsonValue.Create(number);
            case uint number:
                return JsonValue.Create(number);
            case long number:
                return JsonValue.Create(number);
            case ulong number:
                return JsonValue.Create(number);
            case float number:
                return JsonValue.Create(number);
            case double number:
                return JsonValue.Create(number);
            case decimal number:
                return JsonValue.Create(number);
            case IDictionary dictionary:
                {
                    if (!active.Add(value))
                    {
                        throw new InvalidOperationException("a cyclic value");
                    }

                    var result = new JsonObject();
                    try
                    {
                        foreach (DictionaryEntry pair in dictionary)
                        {
                            if (pair.Key is not string key)
                            {
                                throw new InvalidOperationException("a dictionary key that is not a string");
                            }

                            result[key] = pair.Value is null ? null : ConvertStructuredValue(pair.Value, active);
                        }
                    }
                    finally
                    {
                        active.Remove(value);
                    }

                    return result;
                }
            case IEnumerable enumerable:
                {
                    if (!active.Add(value))
                    {
                        throw new InvalidOperationException("a cyclic value");
                    }

                    var result = new JsonArray();
                    try
                    {
                        foreach (var item in enumerable)
                        {
                            result.Add(item is null ? null : ConvertStructuredValue(item, active));
                        }
                    }
                    finally
                    {
                        active.Remove(value);
                    }

                    return result;
                }
            default:
                throw new InvalidOperationException("a non-JSON value");
        }
    }
}
