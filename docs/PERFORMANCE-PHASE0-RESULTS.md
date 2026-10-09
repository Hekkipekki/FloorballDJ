# Phase 0: initial implementation and desktop observations

3 October 2026. Source base `a8f9b3dc85bc468ce49fae0b9a5f07c2f135a180` plus uncommitted Phase 0 changes; app version remains `0.40.0-rc.1`. Diagnostic schema 1, revision `phase0-v1`.

The first implementation package is ready: optional timing, a repeatable runner, bounded capture, report generation, and a portable Windows x64 Release kit. This establishes measurement tools and an exploratory desktop reference. **The full M0 measurement milestone remains open**, and no production performance optimization has been made.

Update, 4 October 2026: the user supplied an actual-profile ProBook capture, and the first hotkey/random-group optimization is now implemented. This document preserves the original Phase 0 observations; see [Phase 1 findings and changes](PERFORMANCE-PHASE1-HOTKEYS.md) for the new evidence and current status. M0 remains open.

## Implemented

- Command/operation IDs correlate managed keyboard or jingle mouse handlers with routing, view-model play, engine preparation, lock wait, decoder/file open, provider construction, seek, endpoint resolution, output construction/initialization/Play, first decoded samples, first returned source-provider buffer, first post-fader signal buffer, and stop/failure/action outcomes.
- Background measurements cover profile loading/counts, save snapshot/bytes/gates/revision replacement, waveform hit/miss/decode, autoplay activation, loudness/music analysis and merge/export. They retain existing scheduling and behavior; this is not yet a complete trace of every feature or background queue wait.
- Capture is disabled by default. Disabled timing hooks allocate no per-call objects in their check. Capture-enabled event producers enqueue without waiting for the writer; an 8,192-event queue drops excess events with accounting, and a 128 MiB event-file cap stops capture. JSON serialization/file writes occur on the background writer. Closing flushes capture; abrupt termination can leave partial files, which the report flags.
- One-second samples record CPU core equivalents, working set, managed heap/allocated bytes, GC counts, handles and threads. A bounded input-priority dispatcher probe records periodic UI queue delay. These are summaries, not CPU stacks, GC pauses, actual callback deadlines or hardware underrun detection.
- Reports separate first observed/repeated jingle use, group relevant media/playback settings, calculate nearest-rank p50/p95/p99/max with sample counts, classify fade-out/rejected actions separately, and flag missing/partial/lost/failed capture coverage.
- The isolated runner supports the actual shortcut handler, direct engine calls, generated silent WAV or supplied media, deep start positions, and capture-on/off controls. Packaged Release binaries include their runtime and matching symbols. Build metadata identifies the source base, uncommitted changes and binary hashes. Normal application captures use the normal startup/license/profile flows.

See [PERFORMANCE-CAPTURE.md](PERFORMANCE-CAPTURE.md) for instructions and limits. No network telemetry or AI dependency was added.

## Desktop reference: one silent-WAV scenario

Hardware: Ryzen 9 7900X, approximately 32 GB RAM, Windows 11 Home build 26200. The app ran without a debugger. Output was the Windows default endpoint, shared WASAPI with the existing requested 50 ms buffer. Active device transport and actual negotiated hardware period were not established. This fixture used a generated silent 48 kHz stereo 16-bit WAV, start 0, -60 dB master, fade override 0 with the existing 30 ms minimum ramp, stop/reset before each synthetic F6, and 250 ms spacing after preparation. A hidden window omits visible cart rendering, while bound controls/timers can still run.

Capture: `artifacts/performance/20261003-214439-desktop-keyboard-wav-v1-d9be9bc3`. See its `report.md`, `report.json`, `events.jsonl`, `session.json`, `benchmark.json`, `hardware.json` and `run.json`.

All **1,001** requested starts succeeded and returned a first source-provider buffer; the trace contains **86,168** events, with zero dropped/discarded events and a normal closing summary. No modifier wait/retry occurred. This validates the runner/capture chain in this scenario; it does not prove physical input reliability. First signal markers are absent because the fixture is silent.

| Observation | Samples | p50 ms | p95 ms | p99 ms | Max ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Managed handler received to first provider buffer, repeated use | 1,000 | 6.182 | 6.977 | 7.590 | 8.863 |
| Managed handler received to first provider buffer, first observed use | 1 | 31.975 | 31.975 | 31.975 | 31.975 |
| Output initialization, all starts | 1,001 | 4.044 | 4.534 | 5.180 | 9.460 |
| Endpoint resolution, all starts | 1,001 | 1.261 | 1.454 | 1.744 | 2.453 |
| File open, all starts | 1,001 | 0.045 | 0.063 | 0.085 | 2.187 |

Within this narrow fixture, per-play output initialization costs more than warm WAV file opening. That supports keeping audio output lifecycle high on the investigation list. It does not establish the laptop's bottleneck or justify changing that lifecycle before behavior/device-recovery checks.

Other development work occurred on the desktop during parts of the capture. The periodic dispatcher probe had a 279.982 ms maximum; its p95 was 1.099 ms. It requires a cleaner run and trace correlation before attributing a stall to a particular app operation. Working set ranged from the first sampled 139,833,344 bytes to a maximum 234,033,152 bytes, ending at 216,281,088 bytes. This short capture, with diagnostic allocations and first-use effects, cannot establish a leak or long-session resource stability.

These are **managed preparation/provider timings**, not hotkey-to-speaker latency. Prior input waiting, output consumption/resampling, buffered silence/fades, endpoint/driver transport and physical audio output remain outside the timing chain. First observed use is not a proven cold disk/app-start measurement. The user's 320 kbit/s files, actual profile, visible UI and laptop were not exercised.

## Capture overhead sanity check

Two fresh packaged-runner runs used the same keyboard/WAV fixture, 100 starts each. They are an exploratory pair, with fewer than 1,000 repeats per case, and include the runner's invocation overhead.

| Capture | Dispatch p50 ms | p95 ms | p99 ms | Mean ms |
| --- | ---: | ---: | ---: | ---: |
| Off | 6.198 | 6.823 | 7.447 | 6.57 |
| On | 6.254 | 6.935 | 8.028 | 6.56 |

Runs are under `artifacts/performance-kit/FloorballDJ-RC1-Performance-20261003-214847-079e53e5/captures/20261003-214955-overhead-off-8f99e975` and `20261003-215050-overhead-on-2b84dfe5`. The similar central timings provide a useful sanity check after correcting the capture code. A single pair does not establish a reliable overhead bound; repeat comparisons under controlled conditions and with real media, effects and sustained polyphony. Diagnostic PCM scanning and JSON writer/GC costs vary with workload and are outside some measured provider durations.

An earlier pilot queried the endpoint's friendly name during every Play and materially perturbed setup. That diagnostic query was removed before these runs. The original `desktop-phase0-keyboard-01` timings are excluded. An intermediate run stopped at 234 plays because the synthetic hotkey encountered global modifier state; it is excluded from the 1,000-repeat baseline. The runner now waits/retries outside measured successful dispatch intervals, and the launcher records process outcome for partial-run reporting.

## Validation

Release solution build passed with zero errors; the existing `NU1510` dependency warning remains. All five existing smoke executables passed: Session, Backup, Appearance, MusicAnalysis and RcSmoke. Those include existing save/profile/session checks and silent-WASAPI fade, cancellation, pause and manual-stop checks.

New PerformanceSmoke checks passed both from source and the packaged self-contained runner: disabled-hook allocation, bounded buffer/drop accounting, nested/async/concurrent command IDs, unchanged sample output/offset/length/EOF, selected-deck versus other-deck duplicate shortcut precedence, zero-duration encoding, privacy, runtime/dispatcher probes, writer failure isolation and capture file cap. Windows PowerShell 5 report checks independently verified known percentile values, first/repeat separation, fade classification, zero durations, loss/cap/truncation warnings and diagnostics-off controls. The 1,001-trigger run and packaged capture-on/off runs passed start/buffer count checks. Direct-engine routing was also exercised as a short smoke scenario, not a qualified baseline.

These tests characterize selected existing behaviors, rather than prove equivalence of every feature. Broad shortcut/focus, team/autoplay/random follow-up, endpoint recovery, queue/loop, numerical DSP and long-session coverage remain in the roadmap.

## Remaining M0 work

1. Use the same kit to capture the actual profile/media on the ProBook and desktop, recording the active wired/event outputs and power/display/storage context. Broaden hardware coverage as machines become available; no ProBook-specific settings or code are proposed.
2. Run each important scenario with at least 1,000 warm triggers and multiple fresh launches for first-use observations. Include mouse/hotkey comparisons, trim/resume positions, fades/effects, polyphony, visible UI and competing background operations.
   Added 4 October 2026: the user reported slow opening of **Slumpmässig låtspelare** and a brief application freeze during live ProBook use. Add first/repeated dialog opening with the actual profile, idle and during playback, with click-to-first-frame/interactive timing and separate construction, file-validation, binding/overview and layout stages. Exact tested build/times/context remain unknown. This is prioritized as [P4.1](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p41-priority-random-player-settings-dialog); it is not a measured result or a completed P1.6 fix.
3. Add a no-leading-silence transient and validated digital/physical loopback or external timing method for real output onset and input-queue delay. Do not treat first returned samples as physical sound.
4. Collect managed/system profiling evidence for CPU stacks, allocations/actual GC pauses, waits, storage and driver scheduling when indicated. Establish real negotiated deadlines and underrun/glitch evidence.
5. Expand characterization for the behavior contract, classify the recurring laptop delay and agree on workload-level targets before selecting and claiming an optimization.
