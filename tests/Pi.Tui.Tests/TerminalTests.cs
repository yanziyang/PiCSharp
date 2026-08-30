using Pi.Tui;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream terminal timeout, normalization, and Kitty negotiation cases.</summary>
public sealed class TerminalTests
{
    [Fact(DisplayName = "uses PI_TUI_ESC_TIMEOUT when configured")]
    public void Uses_pi_tui_esc_timeout_when_configured()
    {
        Assert.Equal(80, ProcessTerminal.ResolveEscapeTimeoutMs(new Dictionary<string, string?>
        {
            ["PI_TUI_ESC_TIMEOUT"] = "80",
        }));
        Assert.Equal(80, ProcessTerminal.ResolveEscapeTimeoutMs(new Dictionary<string, string?>
        {
            ["PI_TUI_ESC_TIMEOUT"] = "80",
            ["SSH_TTY"] = "/dev/pts/1",
        }));
    }

    [Fact(DisplayName = "ignores invalid PI_TUI_ESC_TIMEOUT values")]
    public void Ignores_invalid_pi_tui_esc_timeout_values()
    {
        foreach (var value in new[] { "abc", "0", "-5", string.Empty })
        {
            Assert.Equal(10, ProcessTerminal.ResolveEscapeTimeoutMs(new Dictionary<string, string?>
            {
                ["PI_TUI_ESC_TIMEOUT"] = value,
            }));
        }
    }

    [Fact(DisplayName = "defaults to 100ms over SSH")]
    public void Defaults_to_100ms_over_ssh()
    {
        Assert.Equal(100, ProcessTerminal.ResolveEscapeTimeoutMs(new Dictionary<string, string?>
        {
            ["SSH_CONNECTION"] = "10.0.0.1 22",
        }));
        Assert.Equal(100, ProcessTerminal.ResolveEscapeTimeoutMs(new Dictionary<string, string?>
        {
            ["SSH_TTY"] = "/dev/pts/1",
        }));
    }

    [Fact(DisplayName = "defaults to 10ms otherwise")]
    public void Defaults_to_10ms_otherwise() =>
        Assert.Equal(10, ProcessTerminal.ResolveEscapeTimeoutMs(new Dictionary<string, string?>()));

    [Fact(DisplayName = "rewrites Return to CSI-u Shift+Enter when native Shift detection is enabled and Shift is pressed")]
    public void Rewrites_return_to_csi_u_shift_enter_when_native_shift_detection_is_enabled_and_shift_is_pressed() =>
        Assert.Equal("\x1b[13;2u", ProcessTerminal.NormalizeNativeShiftEnterInput("\r", true, true));

    [Fact(DisplayName = "leaves Return unchanged when native Shift detection is disabled")]
    public void Leaves_return_unchanged_when_native_shift_detection_is_disabled() =>
        Assert.Equal("\r", ProcessTerminal.NormalizeNativeShiftEnterInput("\r", false, true));

    [Fact(DisplayName = "leaves Return unchanged when Shift is not pressed")]
    public void Leaves_return_unchanged_when_shift_is_not_pressed() =>
        Assert.Equal("\r", ProcessTerminal.NormalizeNativeShiftEnterInput("\r", true, false));

    [Fact(DisplayName = "leaves non-Return input unchanged")]
    public void Leaves_non_return_input_unchanged()
    {
        Assert.Equal("\x1b[13;2u", ProcessTerminal.NormalizeNativeShiftEnterInput("\x1b[13;2u", true, true));
        Assert.Equal("a", ProcessTerminal.NormalizeNativeShiftEnterInput("a", true, true));
    }

    [Fact(DisplayName = "rewrites Apple Terminal Return to CSI-u Shift+Enter when Shift is pressed")]
    public void Rewrites_apple_terminal_return_to_csi_u_shift_enter_when_shift_is_pressed() =>
        Assert.Equal("\x1b[13;2u", ProcessTerminal.NormalizeAppleTerminalInput("\r", true, true));

    [Fact(DisplayName = "leaves Apple Terminal Return unchanged when Shift is not pressed")]
    public void Leaves_apple_terminal_return_unchanged_when_shift_is_not_pressed() =>
        Assert.Equal("\r", ProcessTerminal.NormalizeAppleTerminalInput("\r", true, false));

    [Fact(DisplayName = "leaves non-Apple Terminal Return unchanged when Shift is pressed")]
    public void Leaves_non_apple_terminal_return_unchanged_when_shift_is_pressed() =>
        Assert.Equal("\r", ProcessTerminal.NormalizeAppleTerminalInput("\r", false, true));

    [Fact(DisplayName = "leaves non-Return input unchanged")]
    public void Leaves_non_return_input_unchanged_for_apple_terminal()
    {
        Assert.Equal("\x1b[13;2u", ProcessTerminal.NormalizeAppleTerminalInput("\x1b[13;2u", true, true));
        Assert.Equal("a", ProcessTerminal.NormalizeAppleTerminalInput("a", true, true));
    }

    [Fact(DisplayName = "queries Kitty mode before enabling modifyOtherKeys fallback")]
    public void Queries_kitty_mode_before_enabling_modify_other_keys_fallback()
    {
        using var harness = NegotiationHarness.Create();
        Assert.Equal("\x1b[>7u\x1b[?u\x1b[c", harness.Writes[0]);
        Assert.DoesNotContain("\x1b[>4;2m", harness.Writes);
        Assert.False(harness.Terminal.KittyProtocolActive);
    }

    [Fact(DisplayName = "activates Kitty mode for non-zero negotiated flags")]
    public void Activates_kitty_mode_for_non_zero_negotiated_flags()
    {
        using var harness = NegotiationHarness.Create();
        harness.Send("\x1b[?7u");

        Assert.Null(harness.LastInput);
        Assert.True(harness.Terminal.KittyProtocolActive);
        Assert.DoesNotContain("\x1b[>4;2m", harness.Writes);
        Assert.DoesNotContain("\x1b[>4;0m", harness.Writes);

        harness.Terminal.Stop();
        Assert.Equal(1, harness.Writes.Count(write => write == "\x1b[<u"));
        Assert.DoesNotContain("\x1b[>4;0m", harness.Writes);
    }

    [Fact(DisplayName = "falls back to modifyOtherKeys for zero Kitty flags")]
    public void Falls_back_to_modify_other_keys_for_zero_kitty_flags()
    {
        using var harness = NegotiationHarness.Create();
        harness.Send("\x1b[?0u");

        Assert.Null(harness.LastInput);
        Assert.False(harness.Terminal.KittyProtocolActive);
        Assert.Equal(1, harness.Writes.Count(write => write == "\x1b[>4;2m"));

        harness.Terminal.Stop();
        Assert.Equal(1, harness.Writes.Count(write => write == "\x1b[>4;0m"));
    }

    [Fact(DisplayName = "falls back to modifyOtherKeys for device attributes without Kitty flags")]
    public void Falls_back_to_modify_other_keys_for_device_attributes_without_kitty_flags()
    {
        using var harness = NegotiationHarness.Create();
        harness.Send("\x1b[?62;4;52c");

        Assert.Null(harness.LastInput);
        Assert.False(harness.Terminal.KittyProtocolActive);
        Assert.Equal(1, harness.Writes.Count(write => write == "\x1b[>4;2m"));
    }

    [Fact(DisplayName = "forwards normal input while waiting for Kitty response")]
    public void Forwards_normal_input_while_waiting_for_kitty_response()
    {
        using var harness = NegotiationHarness.Create();
        harness.Send("a");

        Assert.Equal("a", harness.LastInput);
        Assert.False(harness.Terminal.KittyProtocolActive);
    }

    [Fact(DisplayName = "tracks split Kitty confirmation")]
    public async Task Tracks_split_kitty_confirmation()
    {
        using var harness = NegotiationHarness.Create();
        harness.Send("\x1b[?7");
        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Null(harness.LastInput);

        harness.Send("u");
        await WaitUntilAsync(() => harness.Terminal.KittyProtocolActive);
        Assert.True(harness.Terminal.KittyProtocolActive);
        Assert.DoesNotContain("\x1b[>4;2m", harness.Writes);
    }

    [Fact(DisplayName = "replays buffered CSI-prefix input when it is not a Kitty response")]
    public async Task Replays_buffered_csi_prefix_input_when_it_is_not_a_kitty_response()
    {
        using var harness = NegotiationHarness.Create();
        harness.Send("\x1b[");
        await Task.Delay(70, TestContext.Current.CancellationToken);
        Assert.Null(harness.LastInput);

        await WaitUntilAsync(() => harness.LastInput == "\x1b[", timeoutMs: 500);
        Assert.Equal("\x1b[", harness.LastInput);
    }

    [Fact(DisplayName = "writes a valid OSC 9;4 clear sequence")]
    public void Writes_a_valid_osc_9_4_clear_sequence()
    {
        using var host = new FakeTerminalHost();
        using var terminal = new ProcessTerminal(host);
        terminal.SetProgress(false);
        Assert.Equal(["\x1b]9;4;0\x07"], host.Writes);
    }

    [Fact(DisplayName = "falls back to COLUMNS and LINES before default dimensions")]
    public void Falls_back_to_columns_and_lines_before_default_dimensions()
    {
        using var host = new FakeTerminalHost();
        using var terminal = new ProcessTerminal(
            host,
            environment: new Dictionary<string, string?>
            {
                ["COLUMNS"] = "123",
                ["LINES"] = "45",
            });

        Assert.Equal(123, terminal.Columns);
        Assert.Equal(45, terminal.Rows);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(2);
        }
    }

    private sealed class NegotiationHarness : IDisposable
    {
        private readonly FakeTerminalHost _host;

        private NegotiationHarness(ProcessTerminal terminal, FakeTerminalHost host)
        {
            Terminal = terminal;
            _host = host;
        }

        public ProcessTerminal Terminal { get; }

        public List<string> Writes => _host.Writes;

        public string? LastInput { get; private set; }

        public static NegotiationHarness Create()
        {
            var host = new FakeTerminalHost();
            var terminal = new ProcessTerminal(host);
            var harness = new NegotiationHarness(terminal, host);
            terminal.Start(data => harness.LastInput = data, static () => { });
            host.Writes.Clear();
            terminal.QueryAndEnableKittyProtocol();
            return harness;
        }

        public void Send(string data) => _host.SendInput(data);

        public void Dispose()
        {
            Terminal.Stop();
            Keys.SetKittyProtocolActive(false);
            _host.Dispose();
        }
    }

    private sealed class FakeTerminalHost : ITerminalHost, IDisposable
    {
        public event Action<string>? Input;
        public event Action? Resize;

        public List<string> Writes { get; } = [];

        public int? Columns { get; set; }
        public int? Rows { get; set; }
        public bool IsRaw { get; private set; }

        public void SetRawMode(bool enabled) => IsRaw = enabled;

        public void Resume() { }

        public void Pause() { }

        public void Write(string data) => Writes.Add(data);

        public void SendInput(string data) => Input?.Invoke(data);

        public void SendResize() => Resize?.Invoke();

        public void Dispose() { }
    }
}
