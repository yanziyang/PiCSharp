using Xunit.Sdk;
using Xunit.v3;

// Pi.Tui.Tests runs serially because two tests mutate process-global environment variables that
// other test classes read while constructing a TUI:
//
//   TuiRenderTests sets TERMUX_VERSION, which TuiMainScreen reads in its constructor to decide
//   whether to skip full redraws on height change, and PI_DEBUG_REDRAW, which turns on redraw
//   logging. Both are restored afterwards, but xunit runs test *classes* in parallel, so with
//   parallelism on, any class constructing a TuiMainScreen at the wrong moment silently picks up
//   Termux behaviour or debug logging.
//
// Reading TERMUX_VERSION once at construction (rather than on every render) already removed the
// worse version of this, where the render thread raced the test thread mid-test. This attribute
// closes the remaining cross-class window.
//
// The tighter fix is to inject both settings instead of reading process globals, but upstream reads
// them from the environment and the port follows the source. Do not remove this attribute without
// making that change first.
[assembly: Parallelization(Mode = ParallelMode.None)]
