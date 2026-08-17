using System.Runtime.CompilerServices;

// Expose internal seams (e.g. WsBridge agent-state tracking) to the test project so
// WS-bridge behavior can be verified over its observable state.
[assembly: InternalsVisibleTo("PiWebui.Tests")]
