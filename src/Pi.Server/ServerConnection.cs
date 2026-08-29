using Pi.Protocol;

namespace Pi.Server;

internal static class ServerConnection
{
    internal static ConnectionState CreateState(
        string id,
        IByteConnection connection,
        uint maxFrameLength,
        CancellationTokenSource handshakeTimeout)
    {
        return new ConnectionState(
            id,
            connection,
            new ClientMessageDecoder(new FrameDecoderOptions { MaxFrameLength = maxFrameLength }),
            handshakeTimeout);
    }

    internal static void MarkClosing(ConnectionState state)
    {
        if (!ConnectionState.IsTerminal(state))
        {
            state.Stage = ConnectionStage.Closing;
        }
    }

    internal static void MarkClosed(ConnectionState state)
    {
        state.Disconnected = true;
        state.Stage = ConnectionStage.Closed;
        state.HandshakeTimeout.Cancel();
    }
}
