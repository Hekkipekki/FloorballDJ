# P1.5: autoplay start independent of full-library refresh

Implemented on 4 October 2026. The [RC1 roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md) identified that autoplay hotkeys awaited a recursive music-folder scan before reading the saved playlist. This step removes that dependency for all supported PCs. Version remains 0.40.0-rc.1; hardware and audible-onset qualification remain open.

## Start and preparation

The hotkey activates the autoplay pane without awaiting available-library refresh. PlaylistService copies no live models: it reads/parses the saved file and validates its media paths on a worker, with one shared admission gate acquired asynchronously before worker submission. The UI then resolves current deck references, applies shuffle/loop/queue gain, replaces the queue and uses the existing PlayNextQueued/audio pipeline. Available-library refresh is scheduled afterward at background dispatcher priority.

Folder-only entries historically inherit the enumerated filename title and share one Jingle reference for repeated paths. To preserve that behavior without a full scan, preparation checks only the ancestors and immediate parent directories of saved playlist members. It preserves literal enumeration paths, case-insensitive comparisons and supported-extension membership. Unrelated directory subtrees are not scanned before start. A very large containing directory or slow/inaccessible storage can still delay targeted validation; this is not an I/O-free start.

Manual playlist loading and saving also perform disk/JSON work off UI. Saving captures an owned entry list before yielding and retains the existing indented version-2 JSON layout. Manual loading always reads fresh data. The default/hotkey path may reuse a parsed definition, with fresh media existence checks every time.

## Bounds and freshness

- Parsed definitions: at most **32 entries**, **4 MiB estimated retained payload**, **30-second maximum reuse age**. Length/UTC modification-time changes, removal, expiry, manual refresh and in-app save invalidate reuse. Oversized definitions remain loadable without cache retention. Same-length external edits that deliberately preserve the timestamp are detectable on expiry or manual load, not guaranteed immediately.
- Available-library scans: at most **one admitted worker per view**. Replacements cancel older waits/work; cancellation is checked for each enumerated file and directory. Profile, view and folder guards reject stale results before applying them.
- Next-file hint: at most **one shared reader**, a **16 KiB temporary buffer**, and **256 KiB read per candidate**, after a successful queue start/advance. No decoder, decoded PCM, open handle or prefix data is retained by the app. The hint warms the OS file cache; it is not prepared audio and does not remove decoder/output initialization. Deep trims and formats whose useful data is elsewhere may gain little. Its latency benefit and interference on weak hardware still need measurement.

Sequential hint selection respects the next index and loop boundary. Shuffle uses a deterministic eligible candidate without consuming a random number, altering play counts or determining the actual next choice. Each hint reads the current file again. Queue mutation, a replacement hint, leaving autoplay, user stop/pause/other playback intent and profile disposal cancel older work. A blocked filesystem call finishes before cancellation can be observed; waiting for admission does not occupy another worker.

## Behavior retained

Version-1 array playlists retain shuffle-off/loop-on defaults. Version-2 object flags, case-sensitive property names, null-entry-list fallback, and historical handling of other object version numbers remain unchanged. Entry order and duplicates are retained; missing media is filtered on each preparation. Missing default playlists leave the queue/settings intact; valid empty/all-missing playlists clear it and apply their flags. Malformed input reports the existing load error.

The first matching live deck jingle wins, including hidden decks, retaining its custom title, trim, effects, play mode, fades and session identity. It is resolved after preparation so edits during worker I/O are included. Folder-only duplicates share a raw-file reference; outside-folder or unsupported-extension existing paths retain their saved title and separate fallback identities. The available “Alla” filter still creates raw-file defaults, while deck filters retain deck-specific clips.

Queue volume clamping, shuffle selection, loop/crossfade timing, preview routing and the audio engine are preserved. Latest load wins. Profile replacement, unload, leaving/reentering autoplay, Stop/Space/Pause/other main playback, folder changes and queue edits prevent stale preparation from replacing the current queue or starting late.

Queue replacement and available-list model creation/filtering still run on UI. They are intentionally kept atomic here to preserve queue behavior. Very large playlists/library lists may still have UI tails; P1.6 and later search/view work must measure those costs independently.

## Verification

`tests/FloorballDJ.PerformanceSmoke/AutoplayChecks.cs` exercises the real managed hotkey through MainWindow with a silent 48 kHz stereo WAV. A deliberately blocked full-library enumerator cannot finish, yet the hotkey starts WASAPI playback and applies folder-only duplicate identity and profile gain. The test then releases the scan. This proves the dependency has been removed, without claiming a numerical laptop or physical-onset speedup.

Other checks cover v1/object version behavior, duplicate/first-hidden-deck identity, live clip/effect edits during preparation, raw/saved titles, outside/unsupported/relative/nested/trailing-root paths, missing/reappearing media, current flags and volume clamps, exact save JSON and detached ownership, cache count/byte/age bounds, malformed-file recovery, latest-load/profile/unload/stop/mode/queue/folder cancellation, UI input while a playlist read is blocked, and bounded prefix/loop/shuffle hints. Captures assert worker versus UI ownership and no lost events or private media paths.

Controlled output is under `artifacts/performance/p1-autoplay-controlled/`. Reproduce with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke -c Release -- --autoplay-checks --output artifacts/performance/p1-autoplay-controlled
```

The portable kit includes **Autoplay-Checks.cmd**. Capture revision **phase1-autoplay-v1** adds read/parse/cache/validation/queue-apply/library-scan/prefix timing and work counts. Full-library scans and prefix reads are listed separately from playback failures and onset chains.

The complete performance checks cover the earlier hotkey, waveform, output, save and metadata packages too. Session, backup, appearance, music-analysis, RC and PowerShell report suites must pass for the packaged source.

## Actual-profile comparison and next work

Compare this kit with the preceding metadata-step kit on the same desktop/laptops, playlist/media/output/power setup. Use a large music folder with unrelated nested directories; trigger autoplay repeatedly and immediately switch playlists, Stop/Space/Pause, change profiles, edit queue entries and preview on the secondary output. Check cold/warm loads, file removal/reappearance and saved deck clips. Capture PlaylistQueueApply and dispatcher delays separately from PlaylistPreparation, AutoplayLibraryScan and audio/output setup. Measure whether the bounded prefix hint helps or competes with playback, especially on slower storage and deep trims. Use real transient/loopback methods for audible onset.

The subsequent [P1.6 repeated UI/volume work](PERFORMANCE-PHASE1-UI-WORK.md) is implemented, retaining meters, animations, queue markings and timing. The first [P4.1 dialog-preparation package](PERFORMANCE-PHASE4-RANDOM-SETTINGS.md) is subsequently implemented; P4.2 visible layout/qualification is the next focused investigation; Phase 2 media preparation/decoder ownership remains next in the audio architecture track. Prepared decoders/PCM, an audio command queue, device/sleep recovery, sample-driven coordination and the broad hardware/session qualification remain open.
