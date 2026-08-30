namespace Pi.AgentCore.Harness.Session;

/// <summary>Search query options.</summary>
public sealed record SessionSearchOptions
{
    /// <summary>Restricts matches to canonical entry types.</summary>
    public IReadOnlyList<string>? EntryTypes { get; init; }

    /// <summary>Maximum number of hits.</summary>
    public int? Limit { get; init; }

    /// <summary>Cancellation token for interactive search.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Search hit identity.</summary>
public record SessionSearchHit
{
    /// <summary>Owning session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Matching entry identifier.</summary>
    public required string EntryId { get; init; }
}

/// <summary>Search hit including projected text.</summary>
public sealed record ScanningSessionSearchHit : SessionSearchHit
{
    /// <summary>Entry timestamp.</summary>
    public long Timestamp { get; init; }

    /// <summary>Projected matching text.</summary>
    public required string Snippet { get; init; }
}

/// <summary>Read-only session projection used by scanning search.</summary>
public interface ISessionSearchReadable
{
    /// <summary>Gets session metadata.</summary>
    Task<SessionMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds entries in chronological pages.</summary>
    Task<IReadOnlyList<Entry>> FindEntriesAsync(EntryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Gets an entry's label.</summary>
    Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Adapts a session to the scanning search projection.</summary>
public sealed class SessionSearchReadable<TMetadata> : ISessionSearchReadable
    where TMetadata : SessionMetadata
{
    private readonly Session<TMetadata> _session;

    /// <summary>Creates an adapter.</summary>
    public SessionSearchReadable(Session<TMetadata> session)
    {
        _session = session;
    }

    /// <inheritdoc />
    public async Task<SessionMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        await _session.GetMetadataAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<Entry>> FindEntriesAsync(EntryQuery query, CancellationToken cancellationToken = default) =>
        _session.FindEntriesAsync(query, cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) =>
        _session.GetLabelAsync(id, cancellationToken);
}

/// <summary>Options for a scanning session search.</summary>
public sealed record ScanningSessionSearchOptions
{
    /// <summary>Page size for each readable.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>Projects an entry and label into searchable text.</summary>
    public Func<SessionMetadata, Entry, string?, string>? ProjectText { get; init; }

    /// <summary>Overrides default matching.</summary>
    public Func<string, SessionSearchCandidate, SessionMetadata, bool>? Match { get; init; }
}

/// <summary>One projected candidate during scanning.</summary>
public sealed record SessionSearchCandidate
{
    /// <summary>Entry identifier.</summary>
    public required string EntryId { get; init; }

    /// <summary>Entry sequence.</summary>
    public long Seq { get; init; }

    /// <summary>Entry type.</summary>
    public required string Type { get; init; }

    /// <summary>Entry timestamp.</summary>
    public long Timestamp { get; init; }

    /// <summary>Search text.</summary>
    public required string Text { get; init; }

    /// <summary>Additional projected fields.</summary>
    public IReadOnlyDictionary<string, object?>? Fields { get; init; }
}

/// <summary>Scanning search interface.</summary>
public interface ISessionSearch
{
    /// <summary>Searches entries in source order.</summary>
    IAsyncEnumerable<ScanningSessionSearchHit> SearchAsync(
        string text,
        SessionSearchOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Searches session entries without opening a provider or index.</summary>
public sealed class ScanningSessionSearch : ISessionSearch
{
    private readonly IReadOnlyList<ISessionSearchReadable> _readables;
    private readonly ScanningSessionSearchOptions _options;

    /// <summary>Creates a search over read-only session projections.</summary>
    public ScanningSessionSearch(
        IEnumerable<ISessionSearchReadable> readables,
        ScanningSessionSearchOptions? options = null)
    {
        _readables = readables.ToArray();
        _options = options ?? new ScanningSessionSearchOptions();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScanningSessionSearchHit> SearchAsync(
        string text,
        SessionSearchOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var actual = options ?? new SessionSearchOptions();
        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || actual.Limit is <= 0 || actual.EntryTypes is { Count: 0 })
        {
            yield break;
        }

        var entryTypes = actual.EntryTypes is null ? null : actual.EntryTypes.ToHashSet(StringComparer.Ordinal);
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        var hitCount = 0;
        foreach (var readable in _readables)
        {
            ThrowIfCancelled(actual.CancellationToken, cancellationToken);
            var metadata = await readable.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (!seenSessions.Add(metadata.Id))
            {
                throw new InvalidOperationException($"Duplicate sessionId: {metadata.Id}");
            }

            long afterSeq = 0;
            while (true)
            {
                ThrowIfCancelled(actual.CancellationToken, cancellationToken);
                var pageSize = actual.Limit ?? _options.PageSize;
                var page = await readable.FindEntriesAsync(
                    new EntryQuery
                    {
                        Order = EntryOrder.OldestFirst,
                        Limit = pageSize,
                        Cursor = new EntryCursor { AfterSeq = afterSeq },
                        Type = actual.EntryTypes is { Count: 1 } ? actual.EntryTypes[0] : null,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var entry in page)
                {
                    if (entryTypes is not null && !entryTypes.Contains(entry.Type))
                    {
                        continue;
                    }

                    var label = await readable.GetLabelAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                    var projectedText = _options.ProjectText?.Invoke(metadata, entry, label) ?? DefaultSearchText(entry, label);
                    var candidate = new SessionSearchCandidate
                    {
                        EntryId = entry.Id,
                        Seq = entry.Seq,
                        Type = entry.Type,
                        Timestamp = entry.Timestamp,
                        Text = projectedText,
                        Fields = label is null ? null : new Dictionary<string, object?> { ["label"] = label },
                    };
                    var matches = _options.Match?.Invoke(normalized, candidate, metadata) ??
                                  projectedText.ToLowerInvariant().Contains(normalized, StringComparison.Ordinal);
                    if (!matches)
                    {
                        continue;
                    }

                    yield return new ScanningSessionSearchHit
                    {
                        SessionId = metadata.Id,
                        EntryId = candidate.EntryId,
                        Timestamp = candidate.Timestamp,
                        Snippet = candidate.Text,
                    };
                    hitCount++;
                    if (actual.Limit is not null && hitCount >= actual.Limit.Value)
                    {
                        yield break;
                    }
                }

                afterSeq = page[^1].Seq;
                if (page.Count < pageSize)
                {
                    break;
                }
            }
        }
    }

    private static string DefaultSearchText(Entry entry, string? label)
    {
        var text = Jsonl.JsonlCodec.EncodeMutation(new EntryMutation
        {
            Seq = entry.Seq,
            Entry = entry,
        }).TrimEnd('\n');
        return label is null ? text : text + " " + label;
    }

    private static void ThrowIfCancelled(CancellationToken local, CancellationToken enumerator)
    {
        if (local.IsCancellationRequested || enumerator.IsCancellationRequested)
        {
            throw new OperationCanceledException(local.IsCancellationRequested ? local : enumerator);
        }
    }
}
