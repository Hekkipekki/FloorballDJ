using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

// Test-only EventPipe phase boundaries; no titles, paths or profile contents.
[EventSource(Name = "FloorballDJ-Analysis")]
internal sealed class AnalysisTraceMarkers : EventSource
{
    internal static readonly AnalysisTraceMarkers Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void OpeningStart(int trial, int active, int nativeThread) => WriteEvent(1, trial, active, nativeThread);
    [Event(2, Level = EventLevel.Informational)]
    public void OpeningReady(int trial, int active, int nativeThread) => WriteEvent(2, trial, active, nativeThread);
    [Event(3, Level = EventLevel.Informational)]
    public void InteractionStart(int trial, int active, int nativeThread, string phase) => WriteEvent(3, trial, active, nativeThread, phase);
    [Event(4, Level = EventLevel.Informational)]
    public void InteractionReady(int trial, int active, int nativeThread, string phase) => WriteEvent(4, trial, active, nativeThread, phase);
    [NonEvent] internal void Start(int trial, bool active) { if (IsEnabled()) OpeningStart(trial, active ? 1 : 0, (int)GetCurrentThreadId()); }
    [NonEvent] internal void Ready(int trial, bool active) { if (IsEnabled()) OpeningReady(trial, active ? 1 : 0, (int)GetCurrentThreadId()); }
    [NonEvent] internal void PhaseStart(int trial, bool active, string phase) { if (IsEnabled()) InteractionStart(trial, active ? 1 : 0, (int)GetCurrentThreadId(), phase); }
    [NonEvent] internal void PhaseReady(int trial, bool active, string phase) { if (IsEnabled()) InteractionReady(trial, active ? 1 : 0, (int)GetCurrentThreadId(), phase); }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
