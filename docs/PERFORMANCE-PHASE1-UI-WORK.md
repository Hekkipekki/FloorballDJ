# P1.6: less repeated UI and volume work

Implemented on 4 October 2026, following [autoplay startup](PERFORMANCE-PHASE1-AUTOPLAY.md). This package addresses P1.6/F8/F12 in the [RC1 roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md). Version remains 0.40.0-rc.1. No hardware-specific defaults or reduced meter, waveform or transition update rate are introduced.

## Changes

MainViewModel still receives the complete primary and preview snapshots. Time, position and meter data remain available at the existing **50 ms** polling interval. Derived title, fade label, active ID, path, paused state, preview title and preview availability notify when their value changes. Cart highlighting and waveform-file/pause bindings use the corresponding stable properties, so time/meter changes do not reevaluate every cart's ID. Remaining/preview time strings are formatted once per time change and notify only when the displayed string changes; seek fractions retain their full precision. All derived values are committed before a snapshot notification, and changing the language refreshes static text even when the snapshot is otherwise equal.

AudioEngine checks current scalar voice inputs during each refresh, retaining the existing next-poll response to live gain, normalization, play-mode, multiple-click and ID edits. It rebuilds the main/preview volume groups and targets only when inputs, membership or relevant settings change. Grouping is shared across a state rebuild rather than repeated for every voice. Already-applied unchanged levels are not rewritten. Changes deferred during a fade remain pending and apply after the existing transition finishes. Main/preview separation, Duck, additive Mix, logical-layer headroom, per-instance polyphony headroom, normalization and queue/monitor gains retain the original arithmetic order.

Polling no longer creates temporary end/stopped arrays or lists unless there is an actual terminal voice to process. Empty primary/preview snapshots are immutable reused values. Snapshot publication itself is preserved, including in idle state. Trimmed ends, natural completion, loop/Space/fade coordination and autoplay/random transition checks keep their existing scheduling. This step does not disable timers when minimized or change audio-thread coordination.

Brush conversion retains the existing color parsing, gradient stops, invalid-input fallbacks and rendering. Parsed brushes are frozen and reused from separate **256-entry** solid/gradient LRUs, keyed by input and current culture. FontFamily resolution retains default, system, bundled and custom URI/source semantics in a **128-entry** LRU. Font-catalog discovery still scans current choices when requested. These are entry bounds, not a total application-memory guarantee.

Each deck/page converter retains at most **four views per source collection**. Ownership uses a weak source table, so an abandoned profile collection is not retained by the converter. Matching requests reuse the view; explicit conversion refreshes membership to include in-place position/layout/order edits. Different sources/ranges have independent views, and the actual collection remains live for drag/drop and add/remove. This removes view construction/subscription churn, not the cost of every membership refresh. It does not introduce a virtualized grid or alter page capacity/appearance.

## Controlled verification

`tests/FloorballDJ.PerformanceSmoke/UiWorkChecks.cs` compares real WPF bindings and rendered brush pixels, exercises source order/layout/replacement/collection lifetimes, and checks actual silent WASAPI voice levels against the former volume expression on both output paths. The numerical comparison uses exact float bits rather than a loose tolerance. It covers live input and identical/changed configuration, main/preview gains, Mix/Duck/multiple layers, deferred fade updates, Space reversal and stop cleanup. Earlier native output/RC suites continue to cover pause, seeking, trimmed/natural completion, loops, fades and polyphony.

The complete packaged check initially stopped when the shortcut fixture encountered desktop modifier keys. Its characterization dispatch now waits/retries for at most ten seconds, matching the existing autoplay fixture's handling of ordinary desktop typing. It still invokes the real handler and fails if the shortcut is not handled; production input behavior is unchanged. This test-harness correction is not a playback optimization or latency result.

The isolated run writes `artifacts/performance/p1-ui-work-controlled/ui-work-checks.json`. Initial controlled results:

| Check | Earlier behavior/reference | Updated behavior |
| --- | --- | --- |
| Cart ID converter calls during 1,000 time/meter frames | 1,000 using the former nested snapshot binding | 0 using the stable ID binding; all 1,000 primary and seek-position updates remain |
| Displayed remaining/preview time notifications | Previously emitted per changed snapshot | 500 each for 1,000 frames spaced 50 ms; displayed precision unchanged |
| Warm 1,000 identical gradient conversions, managed bytes on caller thread | 1,488,000 for the former construction routine | 0 for cached conversions; rendered pixels match |
| 1,000 repeated requests for an already-created page view | A new view per conversion | Reuse the one existing view; four retained views per source maximum |
| 1,000 empty primary/preview polling ticks | Previously constructed fresh empty snapshots/iteration helpers | 0 caller-thread bytes and 0 volume rebuilds; 1,000 primary publications retained |
| 1,000 stable ticks with three actual voices | 3,000 executions of the former target expression | 0 target recalculations/rebuilds on both output paths |

These are work/allocation checks, not a measured laptop CPU percentage, total-process allocation reduction or song-onset comparison. Reference expressions and the old WPF binding are evaluated in the current isolated fixture; this is not a full prior-release hardware baseline. Actual-profile rendering/layout costs, interference, GC pauses and sustained resource use remain to be measured.

Reproduce with:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke -c Release -- --ui-work-checks --output artifacts/performance/p1-ui-work-controlled
```

The portable kit includes **UI-Work-Checks.cmd**. Diagnostic revision **phase1-ui-work-v1** adds `VolumeStateRebuild` timing and `VolumeTargetCount`; reports sum calculated targets across changed states separately from polling, playback failures and onset chains. UI/resource/allocation facts are in the controlled check JSON. Keep application/build symbols and capture revisions matched when comparing kits.

## Remaining qualification and next step

Compare the preceding autoplay-step kit and this build with the same actual profile/media and power/output setup on the desktop and weaker laptops. Measure idle, one-track playback, main plus preview, multiple voices, crossfades, live gain/mode edits, cart/page switching and large queue/library views. Check title, active highlight, time/seek, pause/fade label, meters, drag order, custom/bundled fonts and gradients. Include visible/minimized/restored and long sessions; no battery or broad hardware improvement is qualified by the synthetic checks alone.

The initial implementation packages P1.1–P1.6 are now present; actual-profile/hardware qualification is still open. During live ProBook use on 4 October, the user also reported slow opening of **Slumpmässig låtspelare** and a brief application freeze. P1.6 does not claim to fix that opening delay. The first [P4.1 dialog-preparation package](PERFORMANCE-PHASE4-RANDOM-SETTINGS.md) is subsequently implemented; P4.2 visible layout/qualification is now the next focused investigation, ahead of the larger audio-ownership changes.

**Phase 2 media preparation and decoder ownership** remains next in the audio architecture track, guided by fresh file-open/seek/provider/output traces. Prepared readers/PCM and ordered playback commands need explicit COM ownership, memory/cancellation bounds and format/trim/loop/preview equivalence. Output reuse remains opt-in until endpoint/sleep, physical onset and wider hardware qualification support a default change. Phase 3 sample-driven coordination and the other Phase 4 tool-specific work remain open.
