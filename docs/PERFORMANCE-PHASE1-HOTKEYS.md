# Phase 1: hotkey and random-group lookup

Implemented 4 October 2026. RC1 source base `a8f9b3dc85bc468ce49fae0b9a5f07c2f135a180` plus the uncommitted measurement and optimization changes. App/profile versions remain unchanged. This delivery's diagnostic revision: `phase1-hotkey-v1`. Subsequent kits also include the [waveform scheduler](PERFORMANCE-PHASE1-WAVEFORMS.md), with its own revision.

This is the first P1.1 delivery: indexed jingle/category/ID lookup, one input-token conversion per routing pass, and random-group membership resolved before live file checks. It reduces repeated work for every PC. No ProBook-specific settings, audio-quality reductions, fade changes, feature switches, or new playback engine were introduced.

## What the supplied ProBook capture establishes

The user supplied `20261003-224325-manual-profile-c8246cd8-20261003T224511Z-1-001.zip`. Its session started at 22:43 UTC on 3 October (00:43 on 4 October in Sweden). The original files were preserved under `artifacts/performance/probook-20261003-224325-c8246cd8` for analysis. Capture revision is `phase0-v1`, with a normal exit/closing summary, 10,423 events, and zero dropped/discarded events.

The laptop reports an Intel i5-10210U (4 cores/8 logical processors), about 16 GB RAM, Intel UHD/MX250 graphics, Windows 11 Home build 26200, and a 256 GB SK hynix drive. The power plan is Balanced; AC/battery and the selected output/transport were not recorded. The loaded match profile had 15 decks, 553 audio jingles and 698 slots. This remains one representative machine, rather than a product hardware baseline.

There were nine playback requests, all from random-group shortcuts: seven successful starts and two fade-out presses. The seven starts used five MP3 and two WAV sources, all 48 kHz stereo. Each was the first observed play of a different jingle; this is not a repeated same-jingle or 1,000-trigger qualification run.

| Successful start | Managed handler to Play request ms | Play request to first provider buffer ms | Handler to first provider buffer ms |
| --- | ---: | ---: | ---: |
| 1 | 312.387 | 313.659 | 626.046 |
| 2 | 334.704 | 40.660 | 375.364 |
| 3 | 373.457 | 154.333 | 527.790 |
| 4 | 364.312 | 53.898 | 418.210 |
| 5 | 548.615 | 83.330 | 631.945 |
| 6 | 570.596 | 60.279 | 630.875 |
| 7 | 566.932 | 72.428 | 639.360 |

Resolving the random-profile shortcut took only 0.04–1.44 ms. The subsequent interval from `RouteResolved` to `ViewModelPlayRequested` was 275–571 ms across all nine requests, including the two fade-out presses. This identifies substantial work before the audio engine. RC1's pool predicate calls `File.Exists` for every audio jingle **before** checking deck/jingle membership. It then scans the profile again to attribute candidates to decks.

That is a code-confirmed unnecessary operation. The original capture does not individually time file validation, membership or selection, nor record group sizes, so it cannot assign all of the interval to filesystem calls or quantify the resulting laptop improvement. The new build separates those stages and records member/available counts to resolve that uncertainty.

Audio initialization also matters: the seven `OutputInitialization` durations ranged from 17.257 to 225.053 ms (median 29.294 ms), and file opening from 19.454 to 47.476 ms (median 29.428 ms). Per-voice output reuse remains a separate architectural investigation. The largest dispatcher probe was 2,367.761 ms during startup; a later 581.365 ms probe overlapped the last playback command. Neither probe is an individual physical key timestamp.

All seven voices returned a first signal buffer. Handler-to-buffer times ranged from 375 to 639 ms; first-signal buffers from 375 to 905 ms. These are digital provider markers, not speaker latency or perceived onset. Existing clip starts, leading silence, gain and deliberate fades still apply. The small sample does not support reliable p95/p99 or a general hardware performance promise.

## Implementation and behavior preservation

`JingleShortcutIndex` owns maps for the current profile's shortcut matches, category shortcuts/names, audio deck members and jingle IDs. Entries retain deck/cart order and object references. Normal jingle lookup visits the matching bucket, including selected-deck preference, instead of unrelated slots. Category lookup merges matching-name and matching-shortcut members in original order. Team defaults and random follow-ups use ID lookup.

The index observes relevant property and collection changes: shortcut/category/path/ID edits, cart/deck moves/add/remove/reset, and replacement collections. Bulk mutations coalesce into one rebuild; layout, load, restore, import and normal edit completion refresh it. A lookup also rebuilds if pending changes remain. Replacing the current project or its Decks collection rebinds the index. Irrelevant title/gain/session/meter changes do not invalidate it; replacing a slot with the same object for a WPF appearance refresh does not force a structural rebuild. Profile replacement and disposal detach subscriptions. Category comparisons retain the active culture's existing semantics.

Autoplay, team and random-profile lists remain read live on each key press, using the single converted input token, so edits/setup changes are immediate. Reserved keys, typing-focus guards, repeat guards, Space handling and routing priority remain in their existing locations.

Random groups now retrieve only selected deck/jingle members before checking files. No positive/negative file-availability cache was added: selected members and follow-ups are revalidated each press, and playback still opens the selected file normally. Validation precedes duplicate-ID removal so a missing earlier occurrence cannot hide a later existing one. Random deck attribution/order, session/unplayed priority, repeat handling and variation policy stay with the existing selector. A group containing every track still requires checks for every member; this change does not eliminate that group's disk cost.

Rebuilds require work proportional to the current profile, and actual candidate handling is proportional to matching members. The index retains only the current profile's model references and lookup data. Warm unrelated-jingle scans are removed; this is not a constant-time guarantee for huge matching groups or every edit.

New timing stages include `ShortcutIndexRebuild`, `RandomPoolMembership`, `RandomPoolFileValidation`, and `RandomPoolSelectionAndPlay` (the last includes its nested playback work). Numeric member/follow-up/available counts help compare identical groups. No titles, file paths, or membership IDs are written by these new events.

## Controlled desktop checks

The routing-only benchmark compares a frozen RC1 scan against the new lookup in one process, with 1,000 measured repeats per synthetic size. It uses an unmatched category shortcut and a last-deck F6 target. No audio or disk access is included in these durations. Indexed numbers below include creating the input token once.

| Populated jingles | RC1 scan p95 ms | Indexed p95 ms | RC1 allocation/trigger bytes | Indexed allocation/trigger bytes |
| --- | ---: | ---: | ---: | ---: |
| 553 | 0.2139 | 0.0003 | 339,258 | 304 |
| 1,000 | 0.5231 | 0.0004 | 421,611 | 304 |
| 5,000 | 0.6674 | 0.0005 | 1,141,216 | 304 |

For a one-member group, deterministic file-check counts were 553 → 1, 1,000 → 1 and 5,000 → 1, with identical ordered results. These demonstrate less work and allocation, not measured disk-speed gains. JIT/tiering, scheduling and synthetic shortcut distributions affect the numbers; this is an exploratory one-process comparison. First index construction was 5.584 ms for the first fixture; later 1,000/5,000-fixture builds were 0.482/2.550 ms in that already-warmed process, so those build times must not be treated as a size trend.

Raw results: `artifacts/performance/p1-hotkey-routing-desktop/shortcut-benchmark.json`. Reproduce with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke/FloorballDJ.PerformanceSmoke.csproj -c Release -- --shortcut-benchmark --output artifacts/performance/shortcut-comparison
```

A short 100-start silent-WAV playback run also passed successful-start/buffer/loss checks. A separate 100-start run of the preserved Phase 0 kit on the same desktop followed it, with both runs using the default output and diagnostics enabled. Repeated handler-to-first-buffer p95 was 15.944 ms for the updated build and 15.943 ms for the previous build. Median output initialization was 13.766/13.734 ms respectively. All starts returned buffers and both captures closed without lost events. This provides no evidence of a playback timing regression in this narrow check, or of an audio-engine speedup; the engine remains unchanged.

The corresponding capture folders are `artifacts/performance/20261003-230101-phase1-hotkey-desktop-30ae8ce3` and `artifacts/performance/20261003-230752-phase0-current-desktop-control-701e0ee7`. These runs are slower than the earlier Phase 0 desktop qualification, including when using the old binary now. The changed environment/output timing is not explained by these captures, so the earlier and current runs must not be treated as a controlled before/after result. This short check exercises a direct-jingle shortcut with a silent fixture, rather than the user's random groups. It is not a 1,000-repeat qualification or a substitute for the actual profile on the laptop.

## Validation and next comparison

Release build and all five existing smoke suites passed: Session, Backup, Appearance, MusicAnalysis and RcSmoke. New checks compare indexed output against frozen RC1 routing over edits, moves, duplicate shortcuts/IDs, category unions/cultures, empty/missing members, shared references, collection/profile replacement and ID changes. They verify live removal/restoration of files, no unrelated probes, and disposed subscription ownership. Real shortcut-handler tests with silent WASAPI verify random/category/direct precedence, empty-pool fallback, repeated random fade-out and active-setup changes. Diagnostics/report checks continue to pass.

Use the new portable kit's **Capture.cmd** on the ProBook with the same profile/output and the same random-group shortcuts. Compare group member counts, `RandomPoolFileValidation`, preparation and output initialization against the original capture. Include ordinary jingle shortcuts and mouse starts as separate cases. This is the next evidence needed before claiming a speedup on that laptop or deciding whether output reuse, active-group availability handling, save work or waveform scheduling should follow.

P1.1 is an initial delivery, not the completion of the entire indexing/scheduling roadmap. File-path/status caches, broad library metadata, prepared audio/output reuse and full hardware/onset qualification remain open. The supplied laptop run advances M0's hardware coverage but does not close it.
