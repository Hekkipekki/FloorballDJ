# P1.3: ordered saves with owned snapshots

Implemented on 4 October 2026. This step follows hotkey indexing, bounded waveform preparation and the opt-in output-reuse prototype. It addresses JSON serialization on the interface thread, identified as F5/P1.3 in the [performance roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md).

Ordinary playback does not request a profile save on every trigger. This change targets responsiveness during edits/autosave, explicit saves, profile changes and closing; it is not a diagnosis of every hotkey delay on the ProBook.

## What changed

`ProjectSaveSnapshot` captures a privately owned model graph synchronously on its owner's thread. Scalar values and immutable strings are copied/shared safely; decks, jingles, settings, page layouts and all autoplay/random/team collections are detached. Model event subscribers are cleared. No worker enumerates the live project or applies copy setters to it. The copy is private to the save request and is never attached to a view.

The worker serializes that copy using the same JSON settings as before. A global submission chain preserves order across service instances and paths before serialization starts, so a smaller, newer request cannot overtake an older one. Failure is reported to its caller and does not strand later submissions. Existing save gates, the ten-second cross-process semaphore wait, temporary output, asynchronous write/flush, revision comparison/retention and final file replacement remain in use. Explicit save requests are awaited and never coalesced away.

The view-model retains the existing **175 ms debounce**. While an autosave is active, it holds at most **one additional captured autosave**, replacing that pending request with the newest copy. Superseded copies have not entered the writer queue. This bounds automatic snapshots per view-model to an active and a pending graph; additional explicit saves remain individually queued and can hold additional copies. Copies and serialized buffers add transient memory, rather than form a persistent cache.

Each request owns its path, project generation and edit version. Delayed captures are dispatched to the model owner's UI thread even if their caller had no synchronization context. Completion updates are applied on that thread and only for the still-current project/path. Failed requests remain dirty for retry. Save As cannot redirect an earlier request, and an earlier completion cannot mark a newer edit as saved.

Close now awaits `FlushSavesAsync`, including new requests arriving while the first flush writes. Profile load/reload, revision restoration and XML/new-project replacement flush requested changes before replacement and cancel obsolete debounce/pending state. Reloading the same file retries the read if edits were saved during it. Disposing a view-model cancels uncaptured work; normal window closing flushes first.

The optimization is enabled for normal and diagnostic launches. Output reuse remains separately opt-in. Profile format, JSON spelling/order/encoding, fades, audio pipeline, license admission and release version remain unchanged. Portable-backup graph preparation and other UI-side serialization outside normal project saving remain separate work.

## Verification and controlled measurements

The new checks cover exact JSON against the previous serialization operation, every persisted scalar/property in populated fixtures, recursive separation of mutable references, null-value parity, detached subscriptions, and subsequent scalar/list/collection mutations. New persisted model shapes must pass these ownership/parity checks before they are safe to save off-thread.

Integration checks exercise older-large/newer-small saves across service instances, exact revision contents, identical-save revision suppression, serialization/filesystem failures followed by successful requests, and temporary-file cleanup. A blocked cross-process gate demonstrates that input-priority work can still run. Four captured autosaves produce **two commits**—the active request and newest pending request—while two intermediate versions are superseded.

Further checks cover edits during close flush, overlapping Save As requests and history ownership, pending edits before profile switch/reload, restoration, XML replacement, failed dirty-profile flush and canceled disposal. Captures verify snapshot work on the owner/UI thread and JSON serialization on workers, including a debounce caller with no synchronization context. Existing session, backup, appearance, music-analysis and RC smoke suites pass. The full performance checks and Windows PowerShell 5 report checks pass.

The desktop comparison below measures just the synchronous work needed before a request can leave the interface thread. Each synthetic fixture has one warm-up per path and 101 observations in alternating order. The baseline performs the old `SerializeToUtf8Bytes`; the new path captures the owned graph. It is a work comparison, not a before/after test of the whole application or total save duration.

| Synthetic slots | Previous UI work, median / p95 | Owned UI capture, median / p95 | Median UI allocations, previous / new |
| --- | ---: | ---: | ---: |
| 698 | 3.7841 / 4.1139 ms | 0.0587 / 0.0873 ms | 1,784,776 / 395,728 bytes |
| 5,000 | 19.2619 / 20.8409 ms | 0.3881 / 15.7811 ms | 12,749,824 / 2,814,976 bytes |

The large fixture retains a substantial tail despite a much lower median. Some observations coincide with GC collection-count changes; those counters cannot identify or measure the pause, and long observations also occur without a count change. Alternating JSON allocations can affect both paths. No collection is removed from the reported distributions. The worker still performs serialization and allocates its JSON buffer, so lower UI allocations do not imply lower total allocations or faster total disk writes. Real profile/content, GC pauses and concurrent playback need measurement on the laptop and desktop.

Raw controlled facts are in `artifacts/performance/p1-save-controlled/save-checks.json`, collected at 11:51:34 UTC on 4 October. Reproduce in an isolated temporary profile with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke/FloorballDJ.PerformanceSmoke.csproj -c Release -p:PerformanceSymbols=true -- --save-checks --output artifacts/performance/p1-save-controlled
```

The portable kit also supplies **Save-Checks.cmd**, which stores the synthetic result under a unique `checks/save-*` folder. It does not load or save the user's normal profile.

## Capture interpretation and remaining work

Revision **`phase1-save-v1`** changes `SaveSnapshot` from UI-side JSON creation to owned graph capture. `SaveSerialization` measures worker serialization, `SaveQueueWait` measures predecessor wait, and `SaveSnapshotBytes` still records JSON bytes. `SaveSuperseded` identifies a captured autosave replaced before submission. Reports show captured, serialized, completed, superseded and failed requests separately; superseded work is not a failed save. Do not compare old and new `SaveSnapshot` durations as identical operations.

Repeat testing exposed production startup running alongside the old diagnostic runner and adding another profile's autosaves. The runner now uses a resources-only WPF application and asserts that startup creates no production windows. Normal application startup/license checks are unchanged. Earlier automatic-run figures are exploratory and should be repeated with this runner; the supplied manual ProBook capture is unaffected. The output comparison is repeated for this delivery using the corrected runner, with its results in the output-reuse report.

On the ProBook, use **Capture.cmd** with a copied test profile. Include editing several carts, changing settings/pages, saving while playing, Save As, switching/reloading profiles and closing after a burst of edits. Compare with the previous kit using the same files/output/power settings; record that earlier automated runs used a different harness. Compare snapshot/serialization, dispatcher delay, save queue/revision work and correctness; keep ordinary playback-only runs separate.

Actual-profile laptop benefit, visible UI responsiveness under competing work, true GC pauses, long-session memory use and the wider hardware matrix remain open. The [P1.4 import/file-management/XML metadata package](PERFORMANCE-PHASE1-METADATA.md), [P1.5 autoplay start](PERFORMANCE-PHASE1-AUTOPLAY.md) and [P1.6 repeated UI/volume work](PERFORMANCE-PHASE1-UI-WORK.md) are subsequently implemented. The first [P4.1 dialog-preparation package](PERFORMANCE-PHASE4-RANDOM-SETTINGS.md) is subsequently implemented; P4.2 visible layout/qualification is the next focused investigation; prepared-media/decoder ownership remains next in the audio architecture track. An audio command queue, device/sleep qualification and playback coordination remain separate roadmap steps.
