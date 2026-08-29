using Pi.Protocol;
using Pi.Server;

using Xunit;

namespace Pi.Server.Tests;

public sealed class ServerTests
{
    [Fact]
    public async Task Sends_fragmented_handshake_and_dispatches_correlated_list_request()
    {
        var service = new FakeService();
        service.Seed("session-1");
        var server = CreateServer(service);
        var connection = new MemoryConnection();
        var handler = server.Accept(connection);

        SendClient(handler, new ClientHello(ProtocolConstants.ProtocolVersion), fragmentSize: 2);
        var hello = Assert.IsType<ServerHello>(await connection.WaitForMessageAsync(
            static message => message is ServerHello,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProtocolConstants.ProtocolVersion, hello.Version);
        Assert.Equal(server.Id, hello.Snapshot.ServerId);
        Assert.Equal("session-1", Assert.Single(hello.Snapshot.Sessions).Id);

        SendClient(handler, new RequestEnvelope("list-1", new ListCommand()));
        var response = Assert.IsType<ResponseEnvelope>(await connection.WaitForMessageAsync(
            static message => message is ResponseEnvelope { Id: "list-1" },
            TestContext.Current.CancellationToken));

        Assert.True(response.Ok);
        var result = Assert.IsType<ListResult>(response.Result);
        Assert.Equal("session-1", Assert.Single(result.Sessions).Id);
        await server.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sends_hello_error_for_invalid_first_message_and_unsupported_version()
    {
        var invalidServer = CreateServer(new FakeService());
        var invalidConnection = new MemoryConnection();
        var invalidHandler = invalidServer.Accept(invalidConnection);
        SendClient(invalidHandler, new RequestEnvelope("request-1", new ListCommand()));

        var invalidHello = Assert.IsType<ServerHelloError>(await invalidConnection.WaitForMessageAsync(
            static message => message is ServerHelloError,
            TestContext.Current.CancellationToken));
        Assert.Equal(ProtocolErrorCode.InvalidRequest, invalidHello.Error.Code);
        Assert.Equal("The first client message must be hello", invalidHello.Error.Message);
        Assert.True(invalidConnection.Closed);

        var versionServer = CreateServer(new FakeService());
        var versionConnection = new MemoryConnection();
        var versionHandler = versionServer.Accept(versionConnection);
        SendClient(versionHandler, new ClientHello(2));

        var versionHello = Assert.IsType<ServerHelloError>(await versionConnection.WaitForMessageAsync(
            static message => message is ServerHelloError,
            TestContext.Current.CancellationToken));
        Assert.Equal(ProtocolErrorCode.Version, versionHello.Error.Code);
        Assert.Equal("Unsupported protocol version 2; expected 1", versionHello.Error.Message);

        await invalidServer.CloseAsync(TestContext.Current.CancellationToken);
        await versionServer.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Creates_attaches_lists_detaches_and_disposes_idle_runtime()
    {
        var service = new FakeService();
        var server = CreateServer(service, serverId: "server-test");
        var connection = new MemoryConnection();
        var handler = server.Accept(connection);
        await CompleteHandshakeAsync(handler, connection);

        SendClient(handler, new RequestEnvelope(
            "create-1",
            new CreateCommand("/work", "Created", null, ThinkingLevel.Medium)));
        var createResponse = Assert.IsType<ResponseEnvelope>(await connection.WaitForMessageAsync(
            static message => message is ResponseEnvelope { Id: "create-1" },
            TestContext.Current.CancellationToken));
        var created = Assert.IsType<CreateResult>(createResponse.Result);

        Assert.True(createResponse.Ok);
        Assert.Equal(service.LastCreatedId, created.Session.Id);
        Assert.Equal("/work", created.Session.Cwd);
        Assert.Equal("Created", created.Session.Name);
        Assert.True(created.Session.Attached);
        Assert.True(created.Session.Locked);
        Assert.NotEmpty(created.Session.Id);

        SendClient(handler, new RequestEnvelope("list-1", new ListCommand()));
        var listResponse = Assert.IsType<ResponseEnvelope>(await connection.WaitForMessageAsync(
            static message => message is ResponseEnvelope { Id: "list-1" },
            TestContext.Current.CancellationToken));
        var listed = Assert.IsType<ListResult>(listResponse.Result);
        var metadata = Assert.Single(listed.Sessions);
        Assert.Equal(created.Session.Id, metadata.Id);
        Assert.Equal("Created", metadata.SessionName);
        Assert.Equal("/work", metadata.Cwd);

        SendClient(handler, new RequestEnvelope("detach-1", new DetachCommand(created.Session.Id)));
        var detachResponse = Assert.IsType<ResponseEnvelope>(await connection.WaitForMessageAsync(
            static message => message is ResponseEnvelope { Id: "detach-1" },
            TestContext.Current.CancellationToken));
        Assert.IsType<DetachResult>(detachResponse.Result);
        Assert.Equal(1, service.LatestRuntime(created.Session.Id).DisposeCount);

        SendClient(handler, new RequestEnvelope(
            "unowned-1",
            new AbortCommand(created.Session.Id)));
        var unowned = Assert.IsType<ResponseEnvelope>(await connection.WaitForMessageAsync(
            static message => message is ResponseEnvelope { Id: "unowned-1" },
            TestContext.Current.CancellationToken));
        Assert.False(unowned.Ok);
        Assert.NotNull(unowned.Error);
        Assert.Equal(ProtocolErrorCode.InvalidRequest, unowned.Error!.Code);

        await server.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shares_one_runtime_and_limits_session_events_to_attached_connections()
    {
        var service = new FakeService();
        service.Seed("session-1");
        var server = CreateServer(service);
        var attachedConnection = new MemoryConnection();
        var unattachedConnection = new MemoryConnection();
        var attachedHandler = server.Accept(attachedConnection);
        var unattachedHandler = server.Accept(unattachedConnection);
        await CompleteHandshakeAsync(attachedHandler, attachedConnection);
        await CompleteHandshakeAsync(unattachedHandler, unattachedConnection);

        SendClient(attachedHandler, new RequestEnvelope("attach-1", new AttachCommand("session-1")));
        await WaitForResponseAsync(attachedConnection, "attach-1");
        var runtime = service.LatestRuntime("session-1");

        var progress = new AssistantDeltaProgress("assistant-1", 0, ContentKind.Text, "hello");
        runtime.EmitProgress(progress);
        var progressMessage = Assert.IsType<EventEnvelope>(await attachedConnection.WaitForMessageAsync(
            static message => message is EventEnvelope
            {
                Event: SessionProgressEvent { SessionId: "session-1" },
            },
            TestContext.Current.CancellationToken));
        Assert.Equal(progress, Assert.IsType<SessionProgressEvent>(progressMessage.Event).Progress);
        Assert.DoesNotContain(
            unattachedConnection.Messages,
            static message => message is EventEnvelope { Event: SessionProgressEvent });

        SendClient(unattachedHandler, new RequestEnvelope("attach-2", new AttachCommand("session-1")));
        await WaitForResponseAsync(unattachedConnection, "attach-2");
        Assert.Single(service.Runtimes["session-1"]);

        SendClient(attachedHandler, new RequestEnvelope(
            "model-1",
            new SetModelCommand("session-1", new ModelRef("test", "large"))));
        var modelResponse = await WaitForResponseAsync(attachedConnection, "model-1");
        Assert.True(modelResponse.Ok);
        Assert.Equal("large", Assert.IsType<SetModelResult>(modelResponse.Result).Session.Model.Id);

        Assert.Contains(
            unattachedConnection.Messages,
            static message => message is EventEnvelope
            {
                Event: SessionSnapshotEvent { Snapshot: { Model.Id: "large" } },
            });

        await server.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_busy_prompt_but_allows_steer_and_abort()
    {
        var service = new FakeService();
        service.Seed("session-1");
        var reported = new List<Exception>();
        var server = CreateServer(service, onError: reported.Add);
        var connection = new MemoryConnection();
        var handler = server.Accept(connection);
        await CompleteHandshakeAsync(handler, connection);
        SendClient(handler, new RequestEnvelope("attach-1", new AttachCommand("session-1")));
        await WaitForResponseAsync(connection, "attach-1");

        SendClient(handler, new RequestEnvelope("prompt-1", new PromptCommand("session-1", "first")));
        await connection.WaitForMessageAsync(
            static message => message is EventEnvelope
            {
                Event: SessionSnapshotEvent { Snapshot.Phase: SessionPhase.Turn },
            },
            TestContext.Current.CancellationToken);

        SendClient(handler, new RequestEnvelope("prompt-2", new PromptCommand("session-1", "second")));
        var busy = await WaitForResponseAsync(connection, "prompt-2");
        Assert.False(busy.Ok);
        Assert.NotNull(busy.Error);
        Assert.Equal(ProtocolErrorCode.Busy, busy.Error!.Code);

        SendClient(handler, new RequestEnvelope("steer-1", new SteerCommand("session-1", "adjust")));
        var steer = await WaitForResponseAsync(connection, "steer-1");
        Assert.True(steer.Ok);
        Assert.Equal("adjust", Assert.Single(service.LatestRuntime("session-1").Steers).Text);

        SendClient(handler, new RequestEnvelope("abort-1", new AbortCommand("session-1")));
        var abort = await WaitForResponseAsync(connection, "abort-1");
        Assert.True(abort.Ok);
        ResponseEnvelope prompt;
        try
        {
            prompt = await WaitForResponseAsync(connection, "prompt-1");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Prompt response was not received. Server errors: {string.Join(" | ", reported.Select(static item => item.ToString()))}",
                error);
        }
        Assert.True(prompt.Ok);
        Assert.Equal(SessionPhase.Idle, Assert.IsType<PromptResult>(prompt.Result).Session.Phase);
        await server.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Maps_safe_errors_and_isolates_error_observer_failures()
    {
        var reported = new List<Exception>();
        var throwingService = new ThrowingService();
        var server = CreateServer(
            throwingService,
            onError: error =>
            {
                reported.Add(error);
                throw new InvalidOperationException("observer failure");
            });
        var connection = new MemoryConnection();
        var handler = server.Accept(connection);
        await CompleteHandshakeAsync(handler, connection);
        throwingService.ThrowOnList = true;

        SendClient(handler, new RequestEnvelope("internal-1", new ListCommand()));
        var response = await WaitForResponseAsync(connection, "internal-1");
        Assert.False(response.Ok);
        Assert.NotNull(response.Error);
        Assert.Equal(ProtocolErrorCode.InternalError, response.Error!.Code);
        Assert.Equal(ServerErrorMessages.InternalServerError, response.Error.Message);
        Assert.NotEmpty(reported);

        var protocolError = ServerProtocol.ToProtocolError(new NotImplementedError());
        Assert.Equal(ProtocolErrorCode.NotImplemented, protocolError.Code);
        Assert.Equal(ServerErrorMessages.NotImplemented, protocolError.Message);
        await server.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Times_out_connections_that_never_send_hello()
    {
        var server = CreateServer(new FakeService(), handshakeTimeout: TimeSpan.FromMilliseconds(10));
        var connection = new MemoryConnection();
        server.Accept(connection);

        var helloError = Assert.IsType<ServerHelloError>(await connection.WaitForMessageAsync(
            static message => message is ServerHelloError,
            TestContext.Current.CancellationToken));
        Assert.Equal(ProtocolErrorCode.InvalidRequest, helloError.Error.Code);
        Assert.Equal("Handshake timeout", helloError.Error.Message);
        Assert.True(connection.Closed);
        await server.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Starts_and_closes_each_listener()
    {
        var first = new FakeListener("memory://one");
        var second = new FakeListener("memory://two");
        var server = new PiServer(
            new FakeService(),
            new PiServerOptions { Listeners = [first, second], ServerId = "listener-test" });

        Assert.Same(server, await server.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["memory://one", "memory://two"], server.Addresses);
        Assert.Equal(1, first.StartCount);
        Assert.Equal(1, second.StartCount);
        await server.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
    }

    [Fact]
    public void Converts_supported_ai_messages_to_protocol_transcripts()
    {
        var assistant = new Pi.Ai.AssistantMessage
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "model-1",
            StopReason = Pi.Ai.StopReasons.ToolUse,
            Timestamp = 123,
            Content =
            [
                new Pi.Ai.TextContent("hello"),
                new Pi.Ai.ThinkingContent("hmm", Redacted: false),
                new Pi.Ai.ToolCall("call-1", "read", new System.Text.Json.Nodes.JsonObject
                {
                    ["path"] = "README.md",
                }),
            ],
        };

        var transcript = ServerProtocol.ToProtocolAssistantMessage(assistant, "message-1");
        Assert.Equal("message-1", transcript.Id);
        Assert.Equal("test-provider", transcript.Model.Provider);
        Assert.Equal(TranscriptStopReason.ToolUse, Assert.IsType<CompleteAssistantTranscriptItem>(transcript).StopReason);
        Assert.Equal(3, transcript.Content.Count);
        Assert.IsType<ToolCallContent>(transcript.Content[2]);

        var toolResult = new Pi.Ai.ToolResultMessage
        {
            ToolCallId = "call-1",
            ToolName = "read",
            Content = [new Pi.Ai.TextContent("result")],
            IsError = false,
            Timestamp = 124,
        };
        var toolTranscript = ServerProtocol.ToProtocolToolResultMessage(
            toolResult,
            "tool-1",
            new Pi.Ai.ToolCall(
                "call-1",
                "read",
                new System.Text.Json.Nodes.JsonObject { ["path"] = "README.md" }));
        Assert.Equal("read", toolTranscript.ToolName);
        Assert.Equal("complete", toolTranscript.Status);
    }

    private static PiServer CreateServer(
        FakeService service,
        string? serverId = null,
        TimeSpan? handshakeTimeout = null,
        Action<Exception>? onError = null) =>
        new(
            service,
            new PiServerOptions
            {
                Listeners = [],
                ServerId = serverId,
                HandshakeTimeout = handshakeTimeout,
                OnError = onError,
            });

    private static async Task<ServerHello> CompleteHandshakeAsync(
        ByteConnectionHandler handler,
        MemoryConnection connection)
    {
        SendClient(handler, new ClientHello(ProtocolConstants.ProtocolVersion));
        return Assert.IsType<ServerHello>(await connection.WaitForMessageAsync(
            static message => message is ServerHello,
            TestContext.Current.CancellationToken));
    }

    private static async Task<ResponseEnvelope> WaitForResponseAsync(
        MemoryConnection connection,
        string requestId) =>
        Assert.IsType<ResponseEnvelope>(await connection.WaitForMessageAsync(
            message => message is ResponseEnvelope response && response.Id == requestId,
            TestContext.Current.CancellationToken));

    private static void SendClient(ByteConnectionHandler handler, ClientMessage message, int? fragmentSize = null)
    {
        var frame = ProtocolCodec.EncodeClientMessage(message);
        var size = fragmentSize.GetValueOrDefault(frame.Length);
        for (var offset = 0; offset < frame.Length; offset += size)
        {
            handler.OnData(frame.AsMemory(offset, Math.Min(size, frame.Length - offset)));
        }
    }

    private sealed class MemoryConnection : IByteConnection
    {
        private readonly object _gate = new();
        private readonly ServerMessageDecoder _decoder = new();
        private readonly List<ServerMessage> _messages = [];
        private TaskCompletionSource<bool> _changed = NewSignal();

        public bool Closed { get; private set; }

        public IReadOnlyList<ServerMessage> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken = default)
        {
            AddFrame(chunk.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(ReadOnlyMemory<byte>? finalChunk = null, CancellationToken cancellationToken = default)
        {
            if (finalChunk is { } chunk && !chunk.IsEmpty)
            {
                AddFrame(chunk.Span);
            }

            lock (_gate)
            {
                Closed = true;
                _changed.TrySetResult(true);
                _changed = NewSignal();
            }

            return ValueTask.CompletedTask;
        }

        public async Task<ServerMessage> WaitForMessageAsync(
            Func<ServerMessage, bool> predicate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            for (; ; )
            {
                Task wait;
                lock (_gate)
                {
                    var message = _messages.FirstOrDefault(predicate);
                    if (message is not null)
                    {
                        return message;
                    }

                    wait = _changed.Task;
                }

                try
                {
                    await wait.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (TimeoutException error)
                {
                    var received = string.Join(", ", Messages.Select(static message => message switch
                    {
                        ResponseEnvelope response => $"response:{response.Id}:{response.Ok}",
                        EventEnvelope envelope => $"event:{envelope.Event.Type}",
                        ServerHello => "hello",
                        ServerHelloError => "hello_error",
                        _ => message.Type,
                    }));
                    throw new InvalidOperationException($"Timed out waiting for server message; received: {received}", error);
                }
            }
        }

        private void AddFrame(ReadOnlySpan<byte> frame)
        {
            var messages = _decoder.Push(frame);
            lock (_gate)
            {
                _messages.AddRange(messages);
                _changed.TrySetResult(true);
                _changed = NewSignal();
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeListener(string address) : IPiServerListener
    {
        public string? Address { get; } = address;
        public int StartCount { get; private set; }
        public int CloseCount { get; private set; }

        public Task StartAsync(ByteConnectionAcceptor accept, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accept);
            StartCount++;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return Task.CompletedTask;
        }
    }

    private class FakeService : IPiServerService
    {
        private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _locked = new(StringComparer.Ordinal);

        public Dictionary<string, List<FakeRuntime>> Runtimes { get; } = new(StringComparer.Ordinal);
        public string? LastCreatedId { get; private set; }

        public virtual Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionMetadata>>(_sessions.Values.Select(ToMetadata).ToArray());

        public Task<IReadOnlyList<ModelMetadata>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelMetadata>>([_testModel]);

        public Task<IPiSessionRuntime> CreateSessionAsync(
            CreateSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            LastCreatedId = options.Id;
            if (_sessions.ContainsKey(options.Id))
            {
                throw new SessionLockedError("Session already exists");
            }

            Seed(
                options.Id,
                options.Name,
                options.Cwd,
                options.Model,
                options.ThinkingLevel);
            return Task.FromResult<IPiSessionRuntime>(Acquire(options.Id));
        }

        public Task<IPiSessionRuntime> OpenSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            if (!_sessions.ContainsKey(sessionId))
            {
                throw new SessionNotFoundError($"Unknown session: {sessionId}");
            }

            if (_locked.Contains(sessionId))
            {
                throw new SessionLockedError($"Session is locked: {sessionId}");
            }

            return Task.FromResult<IPiSessionRuntime>(Acquire(sessionId));
        }

        public void Seed(
            string id,
            string? name = null,
            string? cwd = null,
            ModelRef? model = null,
            ThinkingLevel? thinkingLevel = null)
        {
            _sessions[id] = NewSnapshot(
                id,
                name ?? $"Session {id}",
                cwd ?? "/tmp/pi-server-conformance",
                model ?? new ModelRef(_testModel.Provider, _testModel.Id),
                thinkingLevel ?? ThinkingLevel.Off);
        }

        public FakeRuntime LatestRuntime(string id) => Runtimes[id][^1];

        protected virtual FakeRuntime Acquire(string id)
        {
            _locked.Add(id);
            var runtime = new FakeRuntime(_sessions, id, () => _locked.Remove(id));
            if (!Runtimes.TryGetValue(id, out var runtimes))
            {
                runtimes = [];
                Runtimes.Add(id, runtimes);
            }

            runtimes.Add(runtime);
            return runtime;
        }

        private static SessionMetadata ToMetadata(SessionSnapshot snapshot) => new(
            snapshot.Id,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            SessionName: snapshot.Name,
            Cwd: snapshot.Cwd);

        private static SessionSnapshot NewSnapshot(
            string id,
            string name,
            string cwd,
            ModelRef model,
            ThinkingLevel thinkingLevel) =>
            new(
                id,
                name,
                cwd,
                1,
                1,
                SessionPhase.Idle,
                model,
                thinkingLevel,
                Attached: false,
                Locked: false,
                Revision: 0,
                Transcript: [],
                QueuedSteer: [],
                QueuedSteerCount: 0);

        private static readonly ModelMetadata _testModel = new(
            "test",
            "small",
            "Test Small",
            "test-api",
            Reasoning: true,
            Input: [ModelInputKind.Text, ModelInputKind.Image],
            ContextWindow: 16_000,
            MaxTokens: 2_000,
            Cost: new ModelCost(0, 0, 0, 0),
            SupportedThinkingLevels: [ThinkingLevel.Off, ThinkingLevel.Medium, ThinkingLevel.High],
            Authenticated: true);
    }

    private sealed class ThrowingService : FakeService
    {
        public bool ThrowOnList { get; set; }

        public override Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(
            CancellationToken cancellationToken = default) =>
            ThrowOnList
                ? Task.FromException<IReadOnlyList<SessionMetadata>>(
                    new InvalidOperationException("secret backend failure"))
                : base.ListSessionsAsync(cancellationToken);
    }

    private sealed class FakeRuntime : IPiSessionRuntime
    {
        private readonly Dictionary<string, SessionSnapshot> _sessions;
        private readonly string _id;
        private readonly Action _onDispose;
        private readonly HashSet<Action<PiSessionRuntimeEvent>> _listeners = [];
        private TaskCompletionSource<bool>? _pendingPrompt;

        internal FakeRuntime(
            Dictionary<string, SessionSnapshot> sessions,
            string id,
            Action onDispose)
        {
            _sessions = sessions;
            _id = id;
            _onDispose = onDispose;
        }

        public List<SteerInput> Steers { get; } = [];
        public int DisposeCount { get; private set; }
        public TaskCompletionSource<bool> Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions[_id]);

        public SessionPhase GetPhase() => _sessions[_id].Phase;

        public async Task PromptAsync(PromptInput input, CancellationToken cancellationToken = default)
        {
            if (GetPhase() != SessionPhase.Idle)
            {
                throw new SessionBusyError("A prompt is already running");
            }

            _pendingPrompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Update(snapshot => snapshot with
            {
                Phase = SessionPhase.Turn,
                Transcript = snapshot.Transcript.Append(
                    new UserTranscriptItem(
                        $"user-{snapshot.Revision + 1}",
                        [new TextContent(input.Text)],
                        snapshot.Revision + 1)).ToArray(),
            });

            var completed = await _pendingPrompt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = _sessions[_id];
            AssistantTranscriptItem assistant = completed
                ? new CompleteAssistantTranscriptItem(
                    $"assistant-{snapshot.Revision + 1}",
                    [new TextContent($"reply:{input.Text}")],
                    snapshot.Model,
                    null,
                    null,
                    snapshot.Revision + 1,
                    TranscriptStopReason.Stop)
                : new AbortedAssistantTranscriptItem(
                    $"assistant-{snapshot.Revision + 1}",
                    [new TextContent(string.Empty)],
                    snapshot.Model,
                    null,
                    null,
                    snapshot.Revision + 1,
                    null);
            Update(current => current with
            {
                Phase = SessionPhase.Idle,
                Transcript = current.Transcript.Append(assistant).ToArray(),
            });
            _pendingPrompt = null;
        }

        public Task SteerAsync(SteerInput input, CancellationToken cancellationToken = default)
        {
            if (GetPhase() == SessionPhase.Idle)
            {
                return Task.FromException(new SessionBusyError("There is no active prompt to steer"));
            }

            Steers.Add(input);
            Update(snapshot => snapshot with
            {
                QueuedSteerCount = snapshot.QueuedSteerCount + 1,
                QueuedSteer = snapshot.QueuedSteer.Append(
                    new UserTranscriptItem(
                        $"steer-{snapshot.Revision + 1}",
                        [new TextContent(input.Text)],
                        snapshot.Revision + 1)).ToArray(),
            });
            return Task.CompletedTask;
        }

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            if (_pendingPrompt is null)
            {
                return Task.FromException(new SessionBusyError("There is no active prompt to abort"));
            }

            _pendingPrompt.TrySetResult(false);
            return Task.CompletedTask;
        }

        public Task SetModelAsync(ModelRef model, CancellationToken cancellationToken = default)
        {
            if (GetPhase() != SessionPhase.Idle)
            {
                return Task.FromException(new SessionBusyError("Session is busy"));
            }

            Update(snapshot => snapshot with { Model = model });
            return Task.CompletedTask;
        }

        public Task SetThinkingAsync(ThinkingLevel thinkingLevel, CancellationToken cancellationToken = default)
        {
            if (GetPhase() != SessionPhase.Idle)
            {
                return Task.FromException(new SessionBusyError("Session is busy"));
            }

            Update(snapshot => snapshot with { ThinkingLevel = thinkingLevel });
            return Task.CompletedTask;
        }

        public Unsubscribe Subscribe(Action<PiSessionRuntimeEvent> listener)
        {
            _listeners.Add(listener);
            return () => _listeners.Remove(listener);
        }

        public Task DisposeAsync(CancellationToken cancellationToken = default)
        {
            DisposeCount++;
            _onDispose();
            Disposed.TrySetResult(true);
            return Task.CompletedTask;
        }

        public void EmitProgress(TranscriptProgress progress)
        {
            foreach (var listener in _listeners.ToArray())
            {
                listener(new PiSessionRuntimeEvent.Progress(progress));
            }
        }

        private void Update(Func<SessionSnapshot, SessionSnapshot> update)
        {
            var current = _sessions[_id];
            _sessions[_id] = update(current) with
            {
                Revision = current.Revision + 1,
                UpdatedAt = current.UpdatedAt + 1,
            };
            foreach (var listener in _listeners.ToArray())
            {
                listener(new PiSessionRuntimeEvent.SnapshotChanged());
            }
        }
    }
}
