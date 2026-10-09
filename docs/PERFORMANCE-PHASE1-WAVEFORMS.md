# Phase 1: bounded waveform preparation

Implemented 4 October 2026 as P1.2, following the [hotkey/random-group optimization](PERFORMANCE-PHASE1-HOTKEYS.md). Source baseline remains `a8f9b3dc85bc468ce49fae0b9a5f07c2f135a180` plus the uncommitted measurement and optimization changes. App and profile versions remain unchanged. This delivery's diagnostic revision is `phase1-waveform-v1`.

## Why this is the next change

The supplied ProBook capture contains seven full waveform decodes lasting 48.704–1,491.200 ms. These jobs did not overlap one another in that short run, and their durations do not prove that waveform work caused the observed hotkey delay. The capture establishes that waveform preparation is meaningful background work on that machine. Code inspection establishes the lifecycle problem independently: the previous control cancelled only its wait, while `ReadPeaks` continued decoding the entire file. Every cache miss started another worker; evicting a task from the 128-entry cache neither cancelled it nor bounded running work. Retained peaks were limited by file count rather than bytes.

Rapid changes and closed property/merge windows could therefore leave obsolete decoding competing for CPU and storage. Addressing this resource ownership problem preserves the audio pipeline while reducing unnecessary work across PCs. No laptop model checks or hardware-specific tuning were added.

## Resulting ownership and limits

All `WaveformControl` instances use one application-owned `WaveformScheduler`, including the main player, full/detail views in properties, and merge waveforms.

| Resource | Shared default / behavior |
| --- | --- |
| Active decoder | At most one; reader creation, reads and disposal stay in the same synchronous worker. |
| Admitted unique jobs | At most 16, including the active job. Additional live views await capacity with cancellation; they do not start another worker. |
| Shared requests | Identical full-path/length/last-write keys share one job and resulting array. |
| Priority | Visible consumers precede other admitted pending jobs, FIFO within each class. Visibility changes update priority; joining a job does not reorder it unnecessarily. |
| Last consumer leaves | Remove pending work immediately, or cancel active work at decoder-block boundaries. Other consumers retain their shared job. |
| Completed cache | LRU, at most 128 entries and 32 MiB of peak payload; both limits apply. |
| Larger result | Still returned to the view, without retaining it in the shared cache. |
| Failure or cancelled result | Never retained as a cache hit; future requests can retry. |
| Unload / reload | Cancel that view's work, release peaks/geometry, then acquire its last requested path when reloaded. |
| Application exit | Cancel active/pending work, release retained entries and wake admission waiters. |

Admission waits remain associated with live views. This is a bound on admitted jobs/decoder work, not a claim that an arbitrary number of simultaneously open views requires fixed memory. The 32 MiB budget counts retained peak-array payload, rather than total process memory: cache metadata, visible controls, geometry, the active reader, its 65,536-sample block and working/result arrays require additional memory. Closing/unloading a control releases its owned peak and geometry references.

Cancellation is cooperative. A native decoder or filesystem read already in progress must return before the next cancellation check; there is no forced thread termination or fixed millisecond cancellation guarantee. An active job that another view still needs is allowed to finish rather than being interrupted for a new view. The worker stops when the queue empties.

## Behavior preservation

Peak density, sample grouping, minimum/maximum calculation, channel handling, partial final peaks and the 65,536-sample read block follow the previous algorithm. No waveform downsampling or visual simplification was introduced. Existing geometry/interpolation, colors, markers, transitions, seeking and progress rendering remain unchanged. The control's generation check prevents old results or failures from replacing the current track.

File keys retain the existing full path, byte length and last-write identity. File changes with unchanged metadata are not detected by this identity; persistent disk caching and stronger fingerprints are outside this delivery. File metadata is still queried by the control before submission, so this does not complete P1.4's UI-side metadata work. Missing files clear the current waveform, and open/decoder failures remain recoverable.

This scheduler covers waveform preparation. Analysis, merge/export and playback decoders retain their current scheduling. The playback engine, output routing, formats, DSP order, gain, fades, queues and profile serialization are unchanged by this step. A broader background resource scheduler remains future work.

## Validation and evidence

Release build and the five existing Session, Backup, Appearance, MusicAnalysis and Rc smoke suites passed. MusicAnalysis includes decoding a generated mix through the waveform characterization entry point. The expanded PerformanceSmoke checks passed for:

- Exact peak equality against a frozen RC1 algorithm on mono 16-bit PCM and mono/stereo float WAV fixtures, including partial final blocks; pre-cancelled and within-read cancellation.
- Sharing between two consumers, preserving a remaining consumer, active orphan cancellation and an immediate same-key retry.
- One active decoder, bounded admitted work, cancellable capacity waits, visibility changes and FIFO when another consumer joins.
- 500 rapid pending replacements: **zero obsolete queued files decoded**, with **one maximum observed active decoder**.
- Byte/count cache limits, LRU promotion, oversized uncached results, error retries and progress after a failure.
- Current-result protection, unloaded controls, reload of an explicit path, changed/missing file metadata, and scheduler disposal.

Controlled work-count results are in `artifacts/performance/p1-waveform-controlled/waveform-checks.json`. These use controlled reader/gate fixtures and reduced queue/cache limits to force boundary cases; they are not real-file speed measurements or audio-onset benchmarks. Reproduce with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke/FloorballDJ.PerformanceSmoke.csproj -c Release -p:PerformanceSymbols=true -- --waveform-checks --output artifacts/performance/waveform-checks
```

A 100-start silent-WAV playback check completed all starts and first buffers with zero lost/discarded capture events. Repeated handler-to-first-buffer p95 was 16.049 ms; median output initialization was 13.755 ms. It used the hidden-window/default-output fixture, with only one waveform decode, and does not establish a real-profile benefit or qualify 1,000 repeated starts. Raw capture: `artifacts/performance/20261004-014737-phase1-waveform-desktop-a1647ec4`. It preceded the final priority/lifecycle polish; the final behavior checks and packaged runner exercise that polish. There is no controlled real-profile before/after claim for this step.

The report tests also passed under Windows PowerShell 5. Scheduler state counters appear in the report's resource table; queue delay and decode durations appear among stages. These counters are emitted on state changes rather than at the process sampler's one-second interval. Capture events contain no waveform file paths or media contents.

## What to look at next

1. Capture the same ProBook profile/groups/output with this kit. Include rapid changes, property full/detail views of the same file, closing/reopening those views, and normal playback while waveforms prepare. Compare hotkey preparation, output initialization, dispatcher delay, decode activity and pending/admission counts. Keep ordinary repeated-start tests separate from rapid changes.
2. Investigate per-voice output initialization next: the existing ProBook trace shows 17.257–225.053 ms for each successful start, before an additional device/speaker path. A long-lived output prototype needs characterization for main/preview routing, mixed formats, overlap/polyphony, fades, stop/pause/seek/loop, natural completion, device changes/unplug and idle/sleep recovery. Preserve the per-voice DSP/limiter order when introducing a mixer; the roadmap describes this as Phase 2. Do not move the whole current `Play` method to a worker without resolving ownership and ordering.
3. [P1.3 save snapshots are now implemented](PERFORMANCE-PHASE1-SAVES.md), with actual-profile qualification open. Continue P1.4 metadata/autoplay preparation when traces show its cost. This waveform change itself does not resolve UI-side serialization, library scans or audio-output setup. The save report also documents correction of the earlier automated runner's production-startup interference; repeat older automatic measurements with the current resources-only runner.

The new kit combines the initial hotkey optimization and this scheduler. It remains a development/test build of RC1. Actual audible latency, relevant hardware coverage and sustained playback/resource qualification remain open.
