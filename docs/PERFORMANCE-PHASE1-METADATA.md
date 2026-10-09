# P1.4: background import, file inspection and relinking

Implemented on 4 October 2026, following the owned/ordered save pipeline. This is the import/file-management/XML metadata package from the [RC1 performance roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md). It targets work that competes with input during these operations. Actual-profile laptop responsiveness and song-onset benefit have not been measured for this change.

## Implemented behavior

AudioMetadataService receives a copied sequence of path strings and admits one duration reader at a time in the shared production instance. Callers wait asynchronously before worker submission; waiting for admission does not occupy a blocked worker. Each AudioFileReader is constructed, inspected and disposed inside the same worker call. Only immutable scalar results return to UI. No decoder crosses COM apartments or becomes a playback reader/PCM cache.

The duration cache retains at most **256 entries**, with **30-second maximum reuse age**. Every request probes current file metadata on the worker. Length/UTC modification-time changes, removal, expiry and failed reads invalidate entries. A file changed while its reader opens is not cached. Duplicate requests can reuse a duration read while existence is checked afresh. Metadata cannot detect a replacement preserving both size and timestamp within that reuse age; playback still opens the actual file and handles missing/unreadable media there. Audio-analysis and waveform caches are separate.

Main-window assignment validates supported/existing files on a worker, retaining RC1's initial admission, case-insensitive deduplication and input order. It prepares up to **16 durations**, applies that batch on the UI owner and yields to input. Existing empty-slot search, occupied/text-slot preservation, row/page expansion, cart fields and active-page selection remain in use. Reader failure assigns zero duration, as before. A prepared batch can inspect up to 15 files beyond the eventual capacity boundary; later batches stop once the deck is full.

Explicit imports queue per main window. Each request owns its intended project/deck/start cart and rechecks ownership before applying. Profile replacement/disposal cancels work; a removed target or closing window rejects results. Each applied batch requests a save before yielding, so interruption preserves already-visible changes through autosave/close flush. Preparing jobs do not modify the project, and closing does not wait for a blocked metadata reader.

AudioLibraryService inspects copied deck-name/title/path data, builds status/problem guidance, deduplicates byte totals, sorts rows and matches relink candidates on workers. Status facts are deduplicated within one inspection and refreshed each time. RC1's exact-name-first, unique-normalized-name, title fallback, fuzzy threshold and ambiguity margin are retained. Folder traversal retains enumeration/tie order and inaccessible-folder handling; cancellation is checked between file/directory/candidate entries.

The file-management window applies at most **16 relink results** before yielding. Each must still refer to the same live project/deck/cart/original path/title. A newer edit wins over stale preparation. Failed duration reads retain the previous cart duration while updating the path, as before. Closing cancels unapplied work; applied batches remain requested for autosave/flush. Late inspection reports are rejected after refresh replacement, profile replacement, edits or close.

XML loading builds and normalizes its private project on a worker and obtains durations through the shared metadata service. The view-model flushes edits before preparation and again before replacement, then applies the graph on its owner. Newer XML requests cancel older unfinished requests; profile replacement/disposal also cancels them. Obsolete requests neither report successful import nor replace the project. The synchronous importer remains available for existing callers; production UI import uses the asynchronous path.

Profile format/XML interpretation, matching rules, audio/DSP/fades, preview routing, licensing and version **0.40.0-rc.1** retain their current behavior. This package is enabled in ordinary and diagnostic launches. Output reuse remains separately opt-in.

## Verification

The isolated resources-only diagnostic runner adds --metadata-checks. Checks cover:

- Real WAV duration equality; copied inputs, duplicate reuse, eviction, expiry, size/time/removal/recreation invalidation, failure recovery and corrupt-file fallback.
- One active reader across concurrent requests, canceled admission/completion, and input-priority work completing while a controlled reader is deliberately blocked.
- Per-cart found/missing counts, unique byte totals, formats, sorting, nearest-existing-folder guidance and fresh state after removal.
- Expected exact/normalized/diacritic/title/fuzzy matches, ambiguous/short-name rejection, valid-file skipping and unchanged-path handling.
- Successful 34-file imports/relinks across batches; order, deduplication, missing/unsupported admission, occupied/text carts, row/page expansion and retained duration on relink decode failure.
- Ordered overlapping imports, stale path edits, rejected results after profile switch/close, and partial import/relink progress preserved through close/flush.
- Persisted XML parity with the synchronous importer, cancellation, latest-request precedence and a loaded profile surviving an older import's completion.

Captured stages verify probes/duration reads, inspection and matching on workers, and application batches on the UI owner, without lost events. **698 duplicate metadata requests require one duration read** in the fixture. This is a work-count check, not an actual-profile before/after measurement; main-window import already deduplicated incoming paths in RC1. The blocked-reader check demonstrates UI progress without a machine-specific timing threshold.

Raw facts: artifacts/performance/p1-metadata-controlled/metadata-checks.json. Reproduce with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke/FloorballDJ.PerformanceSmoke.csproj -c Release -p:PerformanceSymbols=true -- --metadata-checks --output artifacts/performance/p1-metadata-controlled
```

The kit's **Metadata-Checks.cmd** stores output in a unique checks/metadata-* folder. It uses isolated temporary profiles, without loading/saving the normal profile. Full performance, session, backup, appearance, music-analysis, RC and Windows PowerShell 5 report checks are also run for this delivery. The existing NU1510 dependency warning remains.

## Capture and remaining qualification

Revision **phase1-metadata-v1** adds MetadataFileProbe, MetadataDurationRead, MetadataQueueWait, MetadataTotal, cache-hit/missing/failure markers, LibraryInspection, LibraryRelinkSearch and UI application-batch durations. Report metadataWork counts every event, including multiple cache hits in one request. Metadata failures are separate from playback/save failures. Save-stage meanings introduced in phase1-save-v1 remain in use. Paths, titles and exception messages do not enter these events.

On the ProBook and desktop, use **Capture.cmd** with a copied profile. Import many real files while playing, refresh statistics, search/relink moved files and import XML while continuing controls. Include cancel/close and profile changes during preparation, then reopen saved profiles to check partial progress. Compare dispatcher delays/application batches with the previous save-step kit under the same files, endpoint and power settings.

Batches bound item count rather than milliseconds: layout/binding work remains on UI and can be expensive. Native decoder opening/directory enumeration cannot be forcibly interrupted; cancellation applies at the next managed boundary. Preparation can still compete with waveform/audio decoding and storage. Real MP3/FLAC/other-codec equality, slow/network storage, large/linked folder trees, memory/GC, actual-profile latency and long sessions need hardware qualification.

The subsequent [P1.5 autoplay step](PERFORMANCE-PHASE1-AUTOPLAY.md) and [P1.6 repeated UI/volume work](PERFORMANCE-PHASE1-UI-WORK.md) are implemented. The first [P4.1 dialog-preparation package](PERFORMANCE-PHASE4-RANDOM-SETTINGS.md) is subsequently implemented; P4.2 visible layout/qualification is the next focused investigation; Phase 2 prepared-media/decoder ownership remains next in the audio architecture track. Other tool-specific metadata/preview/editor/planning work remains in Phase 4; this package covers main import, file inspection/relink and XML replacement.
