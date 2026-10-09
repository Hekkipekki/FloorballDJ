using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

if (args.Length != 2) throw new ArgumentException("Usage: FloorballDJ.TraceAnalysis <marked.nettrace> <new-output.json>");
if (File.Exists(args[1])) throw new IOException("Use a new output path to retain earlier analysis.");
var path = args[0];
var etlx = TraceLog.CreateFromEventPipeDataFile(path);
using var log = new TraceLog(etlx);
var phases = new List<Phase>();
var counts = new Dictionary<string, int>();
foreach (var item in log.Events)
{
    var key = item.ProviderName + "/" + item.EventName;
    counts[key] = counts.GetValueOrDefault(key) + 1;
    if (item.ProviderName == "FloorballDJ-Analysis")
    {
        var name = item.EventName.Replace("/", "");
        if (name is not ("OpeningStart" or "OpeningReady" or "InteractionStart" or "InteractionReady")) continue;
        var trial = Convert.ToInt32(item.PayloadByName("trial")); var active = Convert.ToInt32(item.PayloadByName("active"));
        var kind = name.StartsWith("Interaction", StringComparison.Ordinal) ? Convert.ToString(item.PayloadByName("phase"))! : "opening";
        if (name.EndsWith("Start", StringComparison.Ordinal))
        {
            if (phases.Any(phase => phase.Trial == trial && phase.Active == active && phase.Kind == kind)) throw new InvalidDataException("Duplicate phase start.");
            phases.Add(new Phase { Trial = trial, Active = active, Kind = kind, Thread = Convert.ToInt32(item.PayloadByName("nativeThread")), Start = item.TimeStampRelativeMSec });
        }
        else
        {
            var phase = phases.LastOrDefault(phase => phase.Trial == trial && phase.Active == active && phase.Kind == kind) ?? throw new InvalidDataException("Ready marker has no start.");
            if (phase.End != 0) throw new InvalidDataException("Duplicate phase ready.");
            if (Convert.ToInt32(item.PayloadByName("nativeThread")) != phase.Thread) throw new InvalidDataException("Phase UI thread changed.");
            phase.End = item.TimeStampRelativeMSec;
        }
    }
}
if (phases.Count == 0 || phases.Select(phase => (phase.Trial, phase.Active, phase.Kind)).Distinct().Count() != phases.Count)
    throw new InvalidDataException("Missing or duplicate analysis phase markers.");
var allocations = new List<object>(); var sampled = new List<object>();
foreach (var phase in phases)
{
    if (phase.End <= phase.Start) throw new Exception("Incomplete phase");
    var types = new Dictionary<string, long>(); var allocFrames = new Dictionary<string, long>();
    var sampleFrames = new Dictionary<string, int>(); var leaves = new Dictionary<string, int>();
    var allocationCount = 0; var withStack = 0; var sampleCount = 0;
    var buckets = new Dictionary<string, long>(); var sampleBuckets = new Dictionary<string, int>();
    foreach (var item in log.Events)
    {
        if (item.ThreadID != phase.Thread || item.TimeStampRelativeMSec < phase.Start || item.TimeStampRelativeMSec > phase.End) continue;
        var allocation = item.EventName.Contains("AllocationTick");
        var sample = item.ProviderName.Contains("SampleProfiler");
        if (!allocation && !sample) continue;
        var names = new List<string>();
        for (var frame = item.CallStack(); frame is not null; frame = frame.Caller) names.Add(frame.CodeAddress.FullMethodName);
        var bucket = names.Any(name => name.Contains("SongWrapPanel")) ? "song-panel" :
            names.Any(name => name.Contains("FormatLine") || name.Contains("TextBlock.Measure")) ? "other-text-layout" :
            names.Any(name => name.Contains("FrameworkTemplate.Load")) ? "other-template-creation" :
            names.Any(name => name.Contains("RandomPlayerSettingsWindow.InitializeComponent")) ? "window-xaml" :
            names.Any(name => name.Contains("RandomPlayerSettingsWindow+<InitializeAsync") || name.Contains("RandomPlayerSettingsWindow.CreateEditor") || name.Contains("RandomPlayerSettingsWindow.CreateDeckEditors")) ? "editor-preparation" : "other";
        if (allocation)
        {
            allocationCount++; if (names.Count > 0) withStack++;
            var amount = Convert.ToInt64(item.PayloadByName("AllocationAmount64") ?? item.PayloadByName("AllocationAmount"));
            var type = Convert.ToString(item.PayloadByName("TypeName")) ?? "unknown";
            types[type] = types.GetValueOrDefault(type) + amount;
            buckets[bucket] = buckets.GetValueOrDefault(bucket) + amount;
            foreach (var name in names.Distinct()) allocFrames[name] = allocFrames.GetValueOrDefault(name) + amount;
        }
        if (sample)
        {
            sampleCount++;
            sampleBuckets[bucket] = sampleBuckets.GetValueOrDefault(bucket) + 1;
            foreach (var name in names.Distinct()) sampleFrames[name] = sampleFrames.GetValueOrDefault(name) + 1;
            if (names.Count > 0) leaves[names[0]] = leaves.GetValueOrDefault(names[0]) + 1;
        }
    }
    allocations.Add(new { phase.Trial, phase.Active, phase.Kind, phase.Thread, phase.Start, phase.End, allocationCount, withStack,
        types = types.OrderByDescending(pair => pair.Value).Select(pair => new { type = pair.Key, sampledBytes = pair.Value }), buckets,
        frames = allocFrames.OrderByDescending(pair => pair.Value).Select(pair => new { frame = pair.Key, inclusiveSampledBytes = pair.Value }) });
    sampled.Add(new { phase.Trial, phase.Active, phase.Kind, sampleCount,
        sampleBuckets, frames = sampleFrames.OrderByDescending(pair => pair.Value).Select(pair => new { frame = pair.Key, inclusiveSamples = pair.Value }),
        leaves = leaves.OrderByDescending(pair => pair.Value).Take(25).Select(pair => new { frame = pair.Key, samples = pair.Value }) });
}
var loss = typeof(TraceLog).GetProperty("EventsLost")?.GetValue(log);
File.WriteAllText(args[1], JsonSerializer.Serialize(new {
    schemaVersion = 1, version = "p4.11-phase-trace-v1", checkedUtc = DateTimeOffset.UtcNow,
    traceEventVersion = typeof(TraceLog).Assembly.GetName().Version?.ToString(), reportedEventsLost = loss,
    counts, phases, allocations, sampled,
    limitation = "Marked synthetic main-view opening and optional interaction phases only, native UI thread selected from markers. Startup/media preparation/hold/close excluded by phase boundaries. Group by Kind and exclude trial -1 for warm comparisons; opening and interactions are different work. Thread-stack samples are counts, not CPU milliseconds; blocking/GC/native work can be included. AllocationTick amounts are weighted allocation samples, not exact bytes/counts by object type or retained memory. Inclusive frames overlap and must not be summed. Buckets use first-match stack ancestry: song panel, other text, other templates, window XAML, editor preparation, other; categories are heuristic attribution, not independent libraries. Empty frame names are unresolved. Nullable reportedEventsLost means unavailable if the parser has no property; zero alone does not qualify all loss/stack/symbol coverage. Profiled phase duration is not the unprofiled baseline. No native/kernel/GPU cause is established."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {phases.Count} marked opening/interaction phases summarized; sampled allocation/stack counts, not timing or native attribution.");
sealed class Phase { public int Trial { get; init; } public int Active { get; init; } public string Kind { get; init; } = "opening"; public int Thread { get; init; } public double Start { get; init; } public double End { get; set; } }
