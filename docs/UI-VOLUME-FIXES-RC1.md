# RC1 theme, Autoplay and live limiter controls

Implemented and checked on 9 October 2026. App version remains 0.40.0-rc.1; diagnostic revision is `ui-volume-fixes-v1`.

## Changes

- Random-player settings explicitly use the dark tab content background and readable song text. Checked cards use an opaque dark green surface, including disabled cards when the entire deck is included. Existing selection, keyboard navigation and virtualized card reuse remain.
- Autoplay's available-library deck filter is a dark dropdown containing All and the configured decks. Existing filtering, search, source refresh and playlist behavior remain.
- The button below the main volume slider temporarily bypasses the primary-output safety limiter. Its checked amber state reads **LIMITER AV** / **LIMITER OFF**. It affects currently playing and newly started primary voices without restarting playback. Preview continues to follow the profile's limiter setting. The toggle is session-only, is not serialized, and resets at application restart; ordinary audio reconfiguration retains the temporary state.
- New profiles default automatic mix headroom to off. Existing explicit profile preferences remain supported. Mix effects can overlay music without reducing that music's target gain. Intentional Duck and PA/Talk attenuation remain separate controls.

## Understanding the supplied loudness settings

Normalization remains enabled and independent of the quick limiter control. A measured -5.7 LUFS song with a -16 LUFS target receives approximately -10.3 dB of normalization gain. Bypassing the limiter does not remove this reduction. The limiter acts on peaks exceeding its configured threshold; disabling it need not make an already normalized song louder. The supplied profile already has automatic mix headroom unchecked. PA/Talk's -12 dB attenuation applies when Talk is active.

The limiter bypass can allow digital clipping. Its tooltip states this next to the live control. Loud playback in a venue has not been qualified by the silent automated checks.

## Verification and portable use

The full performance regression suite passes, including the new presentation checks. `--presentation-checks --output <fresh-folder>` verifies selected/unselected card colors and titles in Swedish/English, all fifteen deck choices, dropdown opening, a live limiter provider, independent primary/preview protection, normalization retention, music-plus-effect gain, profile serialization, restart state and the real main-window toggle at 1366 x 768. It writes rendered screenshots and `presentation-fix-checks.json`.

The portable kit includes this document and a self-contained Windows app. Extract the complete archive and start `app/FloorballDJ.exe` for ordinary use. Close any older running copy first. For the next ProBook capture, use `Legacy-Capture.cmd` as before. Confirm that both selection states are readable, select a deck in Autoplay, toggle the limiter during playback, and overlay an effect set to Mix. Capture results and live listening remain necessary to assess device/venue behavior and physical latency.
