# Optional primary-output keep-alive — 9 October 2026

User observation: a 3.5 mm analog connection to venue audio produced electrical/static noise, and playing a muted VLC video in the background prevented it. This suggests audio-device/driver idle as a hypothesis; the user confirmed that noise appeared after silence between songs, but the noise and its cause have not been reproduced or proven. Microsoft's [audio inactivity timer documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-device-class-inactivity-timer-implementation) describes audio idle transitions, and its [audio subsystem power-management documentation](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/audio-subsystem-power-management-for-modern-standby-platforms) describes active streaming and runtime idle detection.

The production playback path closes each stopped voice's output. The opt-in reuse prototype retains stopped outputs for at most 30 seconds; it does not continuously render silence. Neither path previously maintained an active render stream between songs.

## Implementation and use

Under Settings > Audio, enable **Håll huvudutgången aktiv / Keep primary output active** and save. It is off by default and stored in each music profile, including snapshots and portable backups. It opens one independent shared-mode WASAPI stream of digital zeroes to the selected primary output (or the Windows default output when none is selected). It requires no video, media file, decoder, generated noise or test tone. Ordinary music volume/normalization, limiter, preview, fades, playlist and random routing remain independent. Stop All stops playback voices while the optional silent stream stays active. Disable the option or close the app to release it.

Opening, stopping and rebuilding the stream happen on a coalesced background worker. Unchanged settings reuse it. Changes of primary endpoint, render default/device notifications and power notifications invalidate it; failure/stopped-stream retries occur on device notification or the five-second timer, without a busy retry loop. A late result after a settings change or close is discarded and disposed. Native device initialization cannot be forcibly cancelled; one in-flight worker cleans up its result. Disabled mode opens no stream and schedules no periodic audio work.

Continuous rendering may increase battery use. Exact consumption and benefit depend on the device/driver. A digital silence stream cannot guarantee prevention of a downstream amplifier's own silence detector, analog interference or a ground-related problem. This change does not diagnose or remove those causes and does not alter system power/driver settings.

Settings draft/copy now also retains the last registered Autoplay playlist path, preventing a later settings save from losing its backup registration.

## Verification and venue test

`--keep-alive-checks --output <fresh-folder>` checks zero-valued silence, default off/no output, exactly one stream, unchanged/changed endpoint behavior, late-close cleanup, failure/retry, real settings binding/copy, profile persistence and real desktop WASAPI silence alongside primary/preview playback and Stop All. Deterministic fixtures simulate unavailable devices and delayed opens. Physical unplug/replug, suspend/resume, actual driver power behavior, long sessions and the venue noise remain unqualified.

Use the same laptop, analog connection and sound-system settings for the venue comparison. Close VLC and enable the option; leave the app silent longer than the interval that previously triggered the noise, then play/fade/stop several songs. Compare with the option off. Note whether noise appears during silence or playback and whether AC/battery changes it. The outcome, rather than the presence of an active software stream, decides whether this resolves the reported problem.
