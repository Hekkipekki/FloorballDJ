# P4.1: random-player settings preparation

Implemented on 4 October 2026 after the user reported slow opening and a brief freeze during live ProBook use. This is the first implementation package for [P4.1](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p41-priority-random-player-settings-dialog). Version remains **0.40.0-rc.1**. The actual laptop observation is still unqualified; its tested build, time and live context were not recorded. No ProBook-specific defaults are introduced.

## What changed and what stays equivalent

The previous constructor built every group against the full project and repeated file-existence checks before showing the window. The new constructor captures detached scalar library inputs and copies the setup/group settings on the model owner's thread. Existing `EnsureLayout` normalization stays on that thread and is timed separately. The window can show its preparation status and Cancel control while **one shared worker** validates files and prepares immutable title/search/duration data. Editing and Save are enabled when preparation finishes. The worker never reads or mutates the live project, UI controls or editable collections.

Each nonblank path is checked once per operation, using Windows path comparison. Available items retain deck/jingle order, distinct identities sharing a file, title fallback, effective clip duration and the original accent/token search normalization. Missing files, empty slots and text blocks remain excluded. Data is fresh for each opening; it is not a cross-window filesystem cache. Creating/duplicating a group/setup, or recreating the last removed group, makes another fresh bounded preparation so newly missing or restored files retain RC1's behavior. Existing groups retain their opening-time availability, as before.

Setup/group editors contain their independent drafts, filtered selection IDs and accurate counts. Only a displayed group's deck/jingle editors are realized. Reading an unvisited summary or saving an unvisited group does not build its controls or editor list. Saving retains the former available-ID filtering, ordering and distinct behavior; the active legacy `RandomPoolProfiles` alias is preserved. Once visited, a group's editable objects and sorting remain stable for the lifetime of that window. This is not a global UI cache or a total-memory bound; visiting every group can still realize every group's list.

Overview requests coalesce on the dispatcher, removing a whole-window rescan for each item in a bulk selection. Selection counts use direct counts rather than temporary included-item arrays. Search reuses normalized text prepared once per available item and allows ordinary WPF layout scheduling to display the best-hit deck instead of forcing synchronous layout. The existing wrap arrangement, item templates, search ranking, ordering, shortcuts/replacement confirmation, follow-ups/fades, deck variation, create/duplicate/remove/rename and Save/Cancel controls remain.

Closing cancels pending work and aborts queued overview updates. Before initial application and Save, ownership and captured library/settings values are checked. Profile replacement and in-place source edits cannot apply a stale result. Failure leaves Cancel available and prevents saving a partially prepared opening. File checks are cooperatively cancellable between calls; a native filesystem call already in progress cannot be forcibly stopped. Ordinary session-count/playback changes do not invalidate the draft.

## Controlled observations

The before run used the preceding P1.6 source plus three scalar work counters. The same fixture generator and manually measured/arranged **1440 x 900** content were used for the after run. Each case has only **three observations**, on the desktop, outside the debugger. These are exploratory medians/work counts, not hardware qualification or click-to-present percentiles. Before construction includes synchronous preparation; after construction returns before the worker. The additional after-ready time includes worker and dispatcher preparation, excluding the explicit fixed-layout measurement.

| Metric | 553 files / 12 groups before | After | 2,000 files / 24 groups before | After |
| --- | ---: | ---: | ---: | ---: |
| File checks at opening | 6,804 | 553 | 49,200 | 2,000 |
| Jingle editors at opening | 6,636 | 553 | 48,000 | 2,000 |
| Constructor median, ms | 106.384 | 5.036 | 904.622 | 14.466 |
| Preparation ready median, ms | Included in before constructor | 16.197 | Included in before constructor | 62.412 |
| Caller-thread bytes, median | 3,044,008 during constructor | 1,416,560 through asynchronous readiness | 18,898,032 during constructor | 4,295,728 through asynchronous readiness |
| Overview refreshes for 40 item changes | 40 | 1 | 40 | 1 |
| Explicit fixed-layout median, ms | 45.464 | 72.755 | 55.979 | 91.791 |

**First layout is not fixed.** The fixed-layout measurement remains over the roadmap's 50 ms investigation threshold in these after runs and increased in this small fixture pair. Dispatcher/binding scheduling differs across the synchronous and asynchronous paths, and three samples cannot establish a stable regression size; this evidence does not support claiming reduced layout or total click-to-visible time. First-use XAML/resource construction also remains: the one-group cold-like first observation was about 171 ms before and 162 ms after, without proving cold OS/JIT state. P4.2 must trace actual visible first paint, translation, templates and list realization next. No broad freeze-free or laptop speed claim follows from reduced file/editor work.

The recorded artifacts are `artifacts/performance/p4-random-settings-controlled/random-settings-before.json`, `random-settings-checks.json` and `random-settings.png`. Copies of the before window and fixture sources are retained beside them. Caller-thread allocation excludes worker/process-wide allocation and can include other dispatcher work. Bulk timing includes a dispatcher yield after the change in the updated fixture; use the work count, not those durations, to assess the coalescing change.

## Verification and diagnostics

`RandomSettingsChecks` verifies one fresh probe per path, worker ownership and cancellation while queued, available-item/order/clip/title/duplicate-path handling, lazy summary/Save parity against the former projection, exact settings and legacy alias behavior, draft/Cancel isolation, search/sort/whole-deck/individual/bulk behavior, fresh additions/duplication/default recreation, source/setting/profile/close rejection and preparation failure. Real silent WASAPI main and preview start while file checking is blocked and the dispatcher continues to process input work. Privacy and complete diagnostic stages are checked. This does not prove audible continuity, every modal confirmation/language/DPI path or long-session resource stability.

The complete performance suite and the Session, Backup, Appearance, MusicAnalysis and RC smoke suites pass. Windows PowerShell 5 report checks include sums of file/editor work across operations and separate ready/stale/cancelled/failed outcomes. Release builds have no errors; the existing NU1510 warning remains.

Run **Random-Settings-Checks.cmd** in the portable kit, or:

```powershell
dotnet run --project tests/FloorballDJ.PerformanceSmoke -c Release -- --random-settings-checks --output artifacts/performance/p4-random-settings-controlled
```

Revision **phase4-random-settings-v1** records normalization, scalar snapshot, XAML construction, preparation queue/file/library work, draft construction, group realization, binding application and overview durations/counts. `RandomSettingsFirstContentRendered` measures the WPF content-rendered event and may describe the initial preparation shell. `RandomSettingsReady` measures the dispatcher reaching context idle after prepared data is bound; it is not proof of compositor presentation or physical screen latency. Neither marker promises that every layout cost is included. Reports keep dialog outcomes separate from playback failures and retain all preceding pipeline metrics. Titles, file paths and exception messages are not written into these events.

Preparation, realization and overview retain the originating opening's operation/command correlation even after its synchronous menu scope has ended. A diagnostic check verifies that the later worker stages belong to that opening; asynchronous work must not be mistaken for an unrelated play request.

## Next: P4.2 visible layout and laptop comparison

**Update 5 October after P4.3:** [stable overview and bounded wrapped song controls are implemented and checked](PERFORMANCE-PHASE4-RANDOM-LAYOUT.md), revision **phase4-random-layout-v1**. The next focused analysis is P4.4's remaining window-show/resources/search-reset work. The original P4.1 table remains historical preparation evidence; actual-profile/hardware and release qualification are still open.

**Update after P4.2:** [the shown-window desktop analysis is complete](PERFORMANCE-PHASE4-LAYOUT-ANALYSIS.md). It corrects late first-deck selection and English-template regressions in revision **phase4-random-settings-v2** and identifies an independent 100%-pool overview width loop plus full selected-deck template realization. The next implementation is P4.3: stable overview measurement, then viewport rendering. The original P4.1 table above remains a historical unshown-layout observation, not a qualified total-opening comparison. Actual laptop/profile/audio/DPI qualification is still open.

Compare this kit with the preceding P1.6 kit using the same actual profile, endpoint, power and display/DPI settings on the ProBook and desktop. Record exact setup/group/deck sizes. Capture first/repeated opening idle and during playback; distinguish constructor/snapshot, worker readiness, first content rendering, populated layout and continuing dispatcher stalls. Profile translation/tree work, templates, nonvirtualized wrap-list realization and repeated setup/group/deck changes before selecting a rendering change. Keep wrapping, item widths, selection/search/sort, keyboard focus, appearance and both languages equivalent.

Recheck Save/Cancel, missing-file changes, close during preparation, preview/main together, transitions and repeated open/close with memory/handle tracking. Use actual-visible frames and a suitable profiler for CPU stacks and GC pauses. The controlled desktop fixtures cannot replace that qualification. P4.2 is the next focused step; Phase 2 prepared-audio/decoder ownership remains a separate architecture track and output reuse remains opt-in.
