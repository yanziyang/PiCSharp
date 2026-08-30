using Pi.AgentCore.Harness.Session.Jsonl;

namespace Pi.AgentCore.Harness.Session;

internal sealed class SessionState
{
    private long _sequence;
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Entry> _entriesById = new(StringComparer.Ordinal);
    private readonly List<LaneRecord> _records = [];
    private readonly Dictionary<string, List<OperationStartedRecord>> _openOperationsByLane = new(StringComparer.Ordinal);
    private readonly List<LanePointer> _lanes;
    private readonly Dictionary<string, LanePointer> _lanesByName;
    private readonly List<LogItem> _log = [];
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private SessionStats _stats = new();
    private string? _name;

    internal SessionState()
    {
        var main = new LanePointer { Lane = "main", LeafId = null };
        _lanes = [main];
        _lanesByName = new Dictionary<string, LanePointer>(StringComparer.Ordinal)
        {
            ["main"] = main,
        };
    }

    internal long NextSequence => _sequence + 1;

    internal IReadOnlyList<LanePointer> GetLanes()
    {
        return _lanes.Select(static lane => lane with { }).ToArray();
    }

    internal string? RequireLane(string lane)
    {
        if (!_lanesByName.TryGetValue(lane, out var pointer))
        {
            throw new SessionError(SessionErrorCode.InvalidLane, $"Lane not found: {lane}");
        }

        return pointer.LeafId;
    }

    internal void ValidateNewLane(string lane)
    {
        if (_lanesByName.ContainsKey(lane))
        {
            throw new SessionError(SessionErrorCode.AlreadyExists, $"Lane already exists: {lane}");
        }
    }

    internal void ValidateTarget(string? targetId)
    {
        if (targetId is not null && !_entriesById.ContainsKey(targetId))
        {
            throw new SessionError(SessionErrorCode.NotFound, $"Entry not found: {targetId}");
        }
    }

    internal void ValidateUnusedId(string id)
    {
        if (_usedIds.Contains(id))
        {
            throw new SessionError(SessionErrorCode.AlreadyExists, $"Session id already exists: {id}");
        }
    }

    internal void ApplyMutation(SessionMutation mutation)
    {
        var sequence = mutation switch
        {
            EntryMutation entry => entry.Entry.Seq,
            RecordMutation record => record.Record.Seq,
            _ => mutation.Seq,
        };
        if (sequence != _sequence + 1)
        {
            InvalidMutation($"has non-consecutive seq {sequence}");
        }

        switch (mutation)
        {
            case EntryMutation entryMutation:
                ApplyEntry(entryMutation, sequence);
                break;
            case RecordMutation recordMutation:
                ApplyRecord(recordMutation, sequence);
                break;
            case LaneMutation laneMutation:
                ApplyLane(laneMutation, sequence);
                break;
            case FactMutation factMutation:
                ApplyFact(factMutation, sequence);
                break;
            default:
                InvalidMutation("has unknown mutation kind");
                break;
        }
    }

    internal Entry? GetEntry(string id) => _entriesById.TryGetValue(id, out var entry) ? CloneEntry(entry) : null;

    internal IReadOnlyList<Entry> FindEntries(EntryQuery query)
    {
        ValidateQuery(query.Limit, query.Cursor?.AfterSeq);
        var results = new List<Entry>();
        foreach (var entry in Ordered(_entries, query.Order))
        {
            if (!MatchesEntry(entry, query))
            {
                continue;
            }

            results.Add(CloneEntry(entry));
            if (query.Limit is not null && results.Count == query.Limit.Value)
            {
                break;
            }
        }

        return results;
    }

    internal IReadOnlyList<Entry> FindEntriesOnBranch(EntryQuery query, string start, BranchBounds bounds)
    {
        ValidateQuery(query.Limit, query.Cursor?.AfterSeq);
        var path = WalkToRoot(start, bounds).ToArray();
        var results = new List<Entry>();
        IEnumerable<Entry> ordered = query.Order == EntryOrder.OldestFirst ? path.Reverse() : path;
        foreach (var entry in ordered)
        {
            var reachedBound = entry.Id == bounds.StopAtId || entry.Type == bounds.StopAtType;
            if (MatchesEntry(entry, query))
            {
                results.Add(CloneEntry(entry));
            }

            if (reachedBound || (query.Limit is not null && results.Count == query.Limit.Value))
            {
                break;
            }
        }

        return results;
    }

    internal IReadOnlyList<LaneRecord> FindRecords(RecordQuery query)
    {
        ValidateQuery(query.Limit, query.AfterSeq);
        var results = new List<LaneRecord>();
        foreach (var record in Ordered(_records, query.Order))
        {
            if (!MatchesRecord(record, query))
            {
                continue;
            }

            results.Add(CloneRecord(record));
            if (query.Limit is not null && results.Count == query.Limit.Value)
            {
                break;
            }
        }

        return results;
    }

    internal IReadOnlyList<OperationStartedRecord> FindOpenOperations(string lane, int? limit)
    {
        ValidateLimit(limit);
        if (!_openOperationsByLane.TryGetValue(lane, out var operations))
        {
            return [];
        }

        var result = operations.AsEnumerable().Reverse();
        if (limit is not null)
        {
            result = result.Take(limit.Value);
        }

        return result.Select(CloneRecord).Cast<OperationStartedRecord>().ToArray();
    }

    internal IReadOnlyList<LogItem> GetLog(LogOptions options)
    {
        ValidateQuery(options.Limit, options.AfterSeq);
        var result = new List<LogItem>();
        foreach (var item in _log)
        {
            if (options.AfterSeq is not null && item.Seq <= options.AfterSeq.Value)
            {
                continue;
            }

            result.Add(CloneLogItem(item));
            if (options.Limit is not null && result.Count == options.Limit.Value)
            {
                break;
            }
        }

        return result;
    }

    internal string? GetName() => _name;

    internal string? GetLabel(string id) => _labels.TryGetValue(id, out var label) ? label : null;

    internal SessionStats GetStats() => _stats with { };

    internal IReadOnlyList<SessionMutation> CreateForkMutations(ForkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<Entry> copiedEntries;
        IReadOnlyList<LanePointer> forkLanes;
        if (options.Scope == "tree")
        {
            copiedEntries = _entries.Select(CloneEntry).ToList();
            forkLanes = GetLanes();
        }
        else
        {
            var selectedEntryId = options.EntryId ?? RequireLane("main");
            string? targetId = null;
            if (selectedEntryId is not null)
            {
                if (!_entriesById.TryGetValue(selectedEntryId, out var selected) || selected is not MessageEntry)
                {
                    throw new SessionError(
                        SessionErrorCode.InvalidForkTarget,
                        $"Fork target is not a message entry: {selectedEntryId}");
                }

                var position = options.Position ?? (options.EntryId is null ? "at" : "before");
                targetId = position == "at" ? selectedEntryId : selected.ParentId;
            }

            copiedEntries = targetId is null
                ? []
                : FindEntriesOnBranch(new EntryQuery { Order = EntryOrder.OldestFirst }, targetId, new BranchBounds()).Select(CloneEntry).ToList();
            forkLanes = [new LanePointer { Lane = "main", LeafId = targetId }];
        }

        var mutations = new List<SessionMutation>();
        long sequence = 1;
        foreach (var entry in copiedEntries)
        {
            mutations.Add(new EntryMutation { Seq = sequence, Entry = entry with { Seq = sequence } });
            sequence++;
        }

        foreach (var pointer in forkLanes)
        {
            mutations.Add(new LaneMutation { Seq = sequence++, Lane = pointer.Lane, LeafId = pointer.LeafId });
        }

        if (_name is not null)
        {
            mutations.Add(new FactMutation { Seq = sequence++, Fact = "name", Name = _name });
        }

        foreach (var entry in copiedEntries)
        {
            if (_labels.TryGetValue(entry.Id, out var label))
            {
                mutations.Add(new FactMutation
                {
                    Seq = sequence++,
                    Fact = "label",
                    TargetId = entry.Id,
                    Label = label,
                });
            }
        }

        return mutations;
    }

    internal static void ValidateQuery(int? limit, long? cursor)
    {
        ValidateLimit(limit);
        if (cursor is < 0)
        {
            throw new SessionError(SessionErrorCode.InvalidQuery, "cursor sequence must be a non-negative integer");
        }
    }

    private static void ValidateLimit(int? limit)
    {
        if (limit is <= 0)
        {
            throw new SessionError(SessionErrorCode.InvalidQuery, "limit must be a positive integer");
        }
    }

    private void ApplyEntry(EntryMutation mutation, long sequence)
    {
        var entry = mutation.Entry;
        if (_usedIds.Contains(entry.Id))
        {
            InvalidMutation($"contains duplicate id {entry.Id}");
        }

        if (mutation.Lane is not null)
        {
            if (!_lanesByName.TryGetValue(mutation.Lane, out var pointer))
            {
                InvalidMutation($"references missing lane {mutation.Lane}");
            }

            if (entry.ParentId != pointer!.LeafId)
            {
                InvalidMutation("does not chain to the lane leaf");
            }
        }

        if (entry.ParentId is not null && !_entriesById.ContainsKey(entry.ParentId))
        {
            InvalidMutation($"references missing parent {entry.ParentId}");
        }

        var stored = CloneEntry(entry);
        _sequence = sequence;
        _usedIds.Add(stored.Id);
        _entries.Add(stored);
        _entriesById[stored.Id] = stored;
        if (mutation.Lane is not null)
        {
            SetLaneLeaf(mutation.Lane, stored.Id);
        }

        _log.Add(new EntryLogItem { Seq = sequence, Entry = CloneEntry(stored) });
        if (stored is MessageEntry)
        {
            _stats = _stats with { MessageCount = _stats.MessageCount + 1 };
        }
    }

    private void ApplyRecord(RecordMutation mutation, long sequence)
    {
        var record = mutation.Record;
        if (!_lanesByName.ContainsKey(record.Lane))
        {
            InvalidMutation($"references missing lane {record.Lane}");
        }

        if (_usedIds.Contains(record.Id))
        {
            InvalidMutation($"contains duplicate id {record.Id}");
        }

        var stored = CloneRecord(record);
        _sequence = sequence;
        _usedIds.Add(stored.Id);
        _records.Add(stored);
        if (stored is OperationStartedRecord started)
        {
            if (!_openOperationsByLane.TryGetValue(stored.Lane, out var operations))
            {
                operations = [];
                _openOperationsByLane[stored.Lane] = operations;
            }

            operations.Add((OperationStartedRecord)CloneRecord(started));
        }
        else if (stored is OperationFinishedRecord finished && _openOperationsByLane.TryGetValue(stored.Lane, out var open))
        {
            var index = open.FindIndex(candidate => candidate.Id == finished.RunId);
            if (index >= 0)
            {
                open.RemoveAt(index);
            }
        }

        _log.Add(new RecordLogItem { Seq = sequence, Record = CloneRecord(stored) });
        if (stored is UsageRecord usage)
        {
            _stats = _stats with
            {
                CachedTokens = _stats.CachedTokens + usage.Usage.CacheRead,
                UncachedTokens = _stats.UncachedTokens + usage.Usage.Input + usage.Usage.CacheWrite,
                TotalTokens = _stats.TotalTokens + usage.Usage.TotalTokens,
                CostTotal = _stats.CostTotal + usage.Usage.Cost.Total,
            };
        }
    }

    private void ApplyLane(LaneMutation mutation, long sequence)
    {
        if (mutation.LeafId is not null && !_entriesById.ContainsKey(mutation.LeafId))
        {
            InvalidMutation($"references missing lane target {mutation.LeafId}");
        }

        _sequence = sequence;
        if (_lanesByName.TryGetValue(mutation.Lane, out var pointer))
        {
            pointer.LeafId = mutation.LeafId;
        }
        else
        {
            var created = new LanePointer { Lane = mutation.Lane, LeafId = mutation.LeafId };
            _lanesByName[mutation.Lane] = created;
            _lanes.Add(created);
        }

        _log.Add(new LaneLogItem { Seq = sequence, Lane = mutation.Lane, LeafId = mutation.LeafId });
    }

    private void ApplyFact(FactMutation mutation, long sequence)
    {
        if (mutation.Fact == "label" && (mutation.TargetId is null || !_entriesById.ContainsKey(mutation.TargetId)))
        {
            InvalidMutation($"references missing label target {mutation.TargetId}");
        }

        _sequence = sequence;
        if (mutation.Fact == "name")
        {
            _name = mutation.Name;
            _log.Add(new NameFactLogItem { Seq = sequence, Name = mutation.Name });
        }
        else
        {
            if (mutation.Label is null)
            {
                _labels.Remove(mutation.TargetId!);
            }
            else
            {
                _labels[mutation.TargetId!] = mutation.Label;
            }

            _log.Add(new LabelFactLogItem
            {
                Seq = sequence,
                TargetId = mutation.TargetId!,
                Label = mutation.Label,
            });
        }
    }

    private void SetLaneLeaf(string lane, string? leafId)
    {
        if (_lanesByName.TryGetValue(lane, out var pointer))
        {
            pointer.LeafId = leafId;
        }
    }

    private static bool MatchesEntry(Entry entry, EntryQuery query)
    {
        return (query.Type is null || entry.Type == query.Type) &&
               (query.CustomType is null || (entry is CustomEntry custom && custom.CustomType == query.CustomType)) &&
               (query.Cursor is null || (query.Order == EntryOrder.OldestFirst
                   ? entry.Seq > query.Cursor.AfterSeq
                   : entry.Seq < query.Cursor.AfterSeq));
    }

    private static bool MatchesRecord(LaneRecord record, RecordQuery query)
    {
        return (query.Lane is null || record.Lane == query.Lane) &&
               (query.Type is null || record.Type == query.Type) &&
               (query.RunId is null || (record is OperationStartedRecord started
                   ? started.Id == query.RunId
                   : record is not (OperationFinishedRecord or StepAttemptRecord or ToolStartedRecord or QueueCancelledRecord or WriteDeferredRecord or UsageRecord)
                       ? false
                       : GetRunId(record) == query.RunId)) &&
               (query.OperationKind is null || record is OperationStartedRecord operation && operation.Intent.Kind == query.OperationKind) &&
               (query.AfterSeq is null || record.Seq > query.AfterSeq.Value);
    }

    private static string? GetRunId(LaneRecord record) => record switch
    {
        OperationFinishedRecord item => item.RunId,
        StepAttemptRecord item => item.RunId,
        ToolStartedRecord item => item.RunId,
        QueueCancelledRecord item => item.RunId,
        WriteDeferredRecord item => item.RunId,
        UsageRecord item => item.RunId,
        _ => null,
    };

    private IEnumerable<Entry> WalkToRoot(string start, BranchBounds bounds)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (!_entriesById.TryGetValue(start, out var current))
        {
            throw new SessionError(SessionErrorCode.NotFound, $"Entry not found: {start}");
        }

        while (true)
        {
            if (!visited.Add(current.Id))
            {
                throw new SessionError(SessionErrorCode.InvalidEntry, $"Session branch contains a cycle at {current.Id}");
            }

            yield return current;
            if (current.Id == bounds.StopAtId || current.Type == bounds.StopAtType || current.ParentId is null)
            {
                yield break;
            }

            if (!_entriesById.TryGetValue(current.ParentId, out current!))
            {
                throw new SessionError(SessionErrorCode.InvalidEntry, $"Entry not found: {current.ParentId}");
            }
        }
    }

    private static IEnumerable<T> Ordered<T>(IReadOnlyList<T> items, EntryOrder order)
    {
        return order == EntryOrder.OldestFirst ? items : items.Reverse();
    }

    private static Entry CloneEntry(Entry entry)
    {
        return JsonlCodec.DecodeEntryObject(SessionJson.EntryToJson(entry, includeStorageFields: true));
    }

    private static LaneRecord CloneRecord(LaneRecord record)
    {
        return JsonlCodec.DecodeRecordObject(SessionJson.RecordToJson(record));
    }

    private static LogItem CloneLogItem(LogItem item) => item switch
    {
        EntryLogItem entry => new EntryLogItem { Seq = entry.Seq, Entry = CloneEntry(entry.Entry) },
        RecordLogItem record => new RecordLogItem { Seq = record.Seq, Record = CloneRecord(record.Record) },
        LaneLogItem lane => new LaneLogItem { Seq = lane.Seq, Lane = lane.Lane, LeafId = lane.LeafId },
        NameFactLogItem name => new NameFactLogItem { Seq = name.Seq, Name = name.Name },
        LabelFactLogItem label => new LabelFactLogItem { Seq = label.Seq, TargetId = label.TargetId, Label = label.Label },
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    private static void InvalidMutation(string message) =>
        throw new SessionError(SessionErrorCode.InvalidEntry, $"Invalid session mutation: {message}");
}
