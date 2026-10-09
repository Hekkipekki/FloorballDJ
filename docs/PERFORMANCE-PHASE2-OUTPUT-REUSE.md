# Phase 2: stopped audio-output reuse prototype

Implemented and checked on the desktop on 4 October 2026. This is the first Phase 2 prototype from the [RC1 performance roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md), following hotkey indexing and bounded waveform preparation. It targets recurring output initialization without changing the per-voice audio pipeline. Phase 2 and release qualification remain open.

The supplied ProBook capture had seven successful starts with output initialization taking 17.257–225.053 ms, median 29.294 ms. Random-group preparation was also expensive and was addressed separately. Those observations justified investigating output ownership next; they do not measure the benefit of this prototype on the laptop.

## Implemented behavior

`AudioOutputPool` retains at most **two stopped, reset outputs globally**, for **30 seconds** after return. A five-second timer retires expired entries; acquisition also checks expiry. Each simultaneous voice retains its own output. Main and preview have separate ownership even when they select the same physical endpoint. There is no mixer, continuously running silent stream, shared mutable decoder, or decoded-media cache in this step.

Reuse requires the same actual resolved endpoint, main/preview route and complete source-format parameters. An incompatible format, unavailable idle output, expiry or invalidation creates a fresh output through the existing WASAPI initialization path. The decoder and all mutable gain/effect/fade/meter providers are constructed separately for every play. A switching provider forwards their bytes and EOF unchanged and detaches the reader before the output becomes idle. Requested output buffering remains 50 ms; fade and buffer-drain behavior retain their existing values.

Endpoint notifications, configured route changes and power-mode events invalidate idle outputs. Active voices can finish, but outputs from an invalidated generation are discarded when returned. Initialization failures, synchronous play failures and asynchronous backend errors cannot enter the idle cache. Engine shutdown stops voices, removes notifications and disposes the pool. Controlled invalidation tests pass; real unplug, default-device changes, format changes and sleep/resume still require hardware testing.

NAudio 2.2.1 sets playback state to Stopped before completing its native Reset and raising its terminal event. The pool waits for that event before rebinding an output. The backend is constructed without a captured UI synchronization context so this confirmation can arrive while the UI is stopping a voice; the lease separately delivers application callbacks to the caller's context. This follows the [version-pinned WasapiOut implementation](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiOut.cs).

Testing also exposed an existing completion race: snapshot/new-command cleanup could remove a naturally ended voice before its queued callback ran, suppressing queue/follow-up advancement. Both output paths now consume terminal events once before cleanup, with a playback epoch to prevent an old notification from affecting a restarted loop. The comparison path still constructs and initializes a fresh output per voice, but includes this shared completion fix; it is not a frozen RC1 binary.

The application enables reuse only with **`--reuse-audio-outputs`**. Normal direct launches retain per-voice initialization. The development kit's **Capture.cmd** and **Silent-Benchmark.cmd** enable the prototype; **Legacy-Capture.cmd** and **Legacy-Silent-Benchmark.cmd** select the comparison path. Profiles, settings UI, license admission and release version are unchanged. This flag is for qualification, not a proposed requirement for users to configure performance modes in the final release.

## Controlled desktop comparison

Two fresh sequential launches used the same Release source, Windows default endpoint, hidden WPF shortcut-handler fixture and generated silent 48 kHz stereo 16-bit WAV. Each run stopped before every trigger and completed **1,001 starts**, with **100 ms** requested spacing after preparation. The first observed start is separated from 1,000 repeats. Diagnostics were enabled, with zero lost/discarded events, zero failures and one first buffer per start. No builds or other test workloads ran concurrently. Both runs used the corrected resources-only diagnostic application, recorded `resources-only-v1`, loaded no production profile and performed just the intended fixture's setup save. Both include the P1.3 save changes.

| Metric | Per-voice comparison | Reuse prototype |
| --- | ---: | ---: |
| Repeated handler → first returned provider buffer, p50 | 15.534 ms | 1.392 ms |
| Same metric, p95 | 15.936 ms | 1.612 ms |
| Same metric, p99 | 16.135 ms | 1.728 ms |
| Same metric, maximum | 21.412 ms | 3.449 ms |
| Output constructions / initializations | 1,001 / 1,001 | 1 / 1 |
| Reused outputs | 0 | 1,000 |
| Median output initialization, where performed | 13.758 ms | 18.180 ms (one observation) |

Repeated first-buffer p95 fell approximately **89.9% in this fixture**. Warm starts skip output initialization; they still resolve the endpoint, open/seek/decode the file, construct providers and start the native playback thread. Cold starts are still cold starts. This is one pair, in one order, on one desktop endpoint. Silent WAV, a hidden window and source-provider timestamps do not establish actual-profile, codec, visible-UI, speaker-onset or broad hardware improvements. The first-buffer boundary precedes output consumption and the hardware path. The unchanged minimum 30 ms ramp also affects audible onset.

Raw runs under `artifacts/performance/`:

- `20261004-115142-save-step-isolated-legacy-54dbb89c`
- `20261004-115426-save-step-isolated-reuse-f0ab326c`

This pair replaces the earlier automatic comparison (`20261004-045841-phase2-legacy-100ms-352a05b3` / `20261004-050123-phase2-reuse-100ms-9355bce4`, p95 15.883 / 1.681 ms) as the current controlled result. Repeat testing exposed normal production startup running alongside the old runner, adding another profile's autosaves. Those older raw runs remain available as exploratory history; the supplied manual ProBook capture is unaffected. The [save report](PERFORMANCE-PHASE1-SAVES.md) explains the runner correction. Normal application startup and license admission are unchanged.

Reports distinguish output lifecycle and actual new/reused output observations, independently of first/repeated jingle use. A repeated file can still require initialization after eviction or when compatible outputs are busy. Process samples remain exploratory: different run lengths and collection timing make final/maximum memory, handles and CPU unsuitable as an idle-cost or leak comparison. No sustained idle/battery qualification is claimed.

## Validation

The performance smoke checks pass, including the new output tests:

- Exact switching-provider bytes, offsets, partial reads and EOF; stopped outputs detach the old source.
- Independent concurrent/main/preview leases; incompatible formats/endpoints never reuse one initialization.
- Two-entry retention, expiry, generation invalidation, disposal and error eviction.
- Delayed stop delivery targets the old lease; loop epochs consume each terminal event once. A controlled Stopped-before-Reset gap blocks reuse/disposal until backend confirmation, on both paths.
- Quiet real WASAPI checks on both paths: repeat fade, reversible Space fade, pause/resume, paused seek, mono/stereo replacement, preview seek/stop, eight-instance polyphony and rejected ninth, natural/trimmed completion, looping and manual stops. Completion callbacks stay on the caller's UI context.

In the behavior fixture, 16 starts required 16 initializations in the comparison path and 10 initializations plus six reuses in the prototype; each path produced two expected natural completions without capture loss or reported failure. Work-count output is recorded in `artifacts/performance/p2-output-behavior/output-reuse-checks.json`. Reproduce it with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke/FloorballDJ.PerformanceSmoke.csproj -c Release -p:PerformanceSymbols=true -- --output-reuse-checks --output artifacts/performance/p2-output-behavior
```

Session, backup, appearance, music-analysis and RC smoke checks passed on the current source both with normal arguments and with the reuse flag; the capture report checks also passed with new/reused output grouping under Windows PowerShell 5. These checks do not constitute full DSP/output-PCM equality, listening, MP3/FLAC preparation, real device recovery, queue/autoplay/team/follow-up end-to-end or long-session coverage. The existing NU1510 dependency warning remains; builds had no errors.

## Next evidence and remaining work

1. Run **Legacy-Capture.cmd**, then **Capture.cmd**, with the same ProBook profile, actual files, output and power settings. Stop between successful starts and record ordinary starts separately from repeated-press fades and rapid changes. Reverse the order in another pair. Record output-init/reuse counts, random preparation, first buffer, dispatcher delay and perceived delay.
2. Test audible output and boundaries with non-silent transients, digital/physical loopback and listening. Cover effects/pitch, Mix/Duck, overlap, long clips, MP3/FLAC, deep seek, preview, queue/autoplay/team/random follow-ups and missing/replaced files. Preserve existing DSP order and gains.
3. Exercise real device unplug/replug, default/explicit output and format changes, idle expiry, sleep/resume, AC/battery and sustained sessions across hardware. Measure resource release and idle cost before considering reuse the production default.
4. Continue Phase 2 reader/prepared-block ownership and ordered command scheduling where traces justify it. A mixer, prepared media and Phase 3 playback coordination are separate steps. [P1.3 saves](PERFORMANCE-PHASE1-SAVES.md), the [P1.4 metadata package](PERFORMANCE-PHASE1-METADATA.md), [P1.5 autoplay start](PERFORMANCE-PHASE1-AUTOPLAY.md) and [P1.6 repeated UI/volume work](PERFORMANCE-PHASE1-UI-WORK.md) are implemented, with actual-profile/hardware qualification still open. The first [P4.1 dialog-preparation package](PERFORMANCE-PHASE4-RANDOM-SETTINGS.md) is subsequently implemented; P4.2 visible layout/qualification is the next focused investigation; prepared-media/decoder ownership remains next in the audio architecture track.

Use the [capture guide](PERFORMANCE-CAPTURE.md) for launch options and measurement limits. The new kit combines prior optimizations with this opt-in prototype; no installer or published release is replaced.
