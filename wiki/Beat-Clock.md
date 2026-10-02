# Beat Clock and Tempo Tracking

LifeViz keeps a continuous musical clock (beat position, tempo, bar alignment) that BPM-synced features read instead of reacting to raw onset events. It replaces the old "time since the last detected hit" timing, which jumped whenever a hi-hat registered as a beat, a beat was missed, or the averaged BPM wobbled.

## Pieces

- **`BeatTracker`** (`BeatTracking.cs`, audio thread). `AudioBeatDetector` feeds it every PCM buffer.
  - **Onset envelope:** a fixed 100 Hz onset-strength envelope. Each sample is the half-wave-rectified log-energy rise of a 150 Hz low-passed band (kick weight 1.0) plus the full band (weight 0.5). It is independent of capture packet size.
  - **Tempo:** autocorrelation of the last 8 s of the envelope. Each candidate beat period is scored by its first four multiples, and the period is refined with parabolic peak interpolation, giving roughly 0.05 BPM accuracy.
  - **Tempo range:** candidates are restricted to a one-octave range (**Detect range**, default 90–180 BPM), so half-time and double-time readings always fold to the same tempo.
  - **Phase:** a comb filter over the last two whole bars finds the most recent beat. Whole bars are used because a partial window sees a different mix of kick, snare and hat beats as it slides, which makes syncopated patterns wobble.
  - **Locking and hysteresis:**
    - It locks once confidence (normalized autocorrelation) reaches 0.16 after at least 4 s of audio.
    - Once locked, it keeps the current tempo unless another one scores at least 25% higher for 3 s.
    - A tempo in a simple ratio with the current one (3:2, 4:3, 5:4, 6:5 and their inverses) needs a score at least 60% higher and confidence of at least 0.35, held for 8 s. Breakdowns, halftime sections and triplet percussion otherwise read as those ratios.
    - It unlocks after 2 s of low confidence and holds the last tempo.
  - **Output:** immutable `BeatEstimate` snapshots (BPM, last beat time, confidence, locked).
- **`BeatClock`** (render thread, advanced once per render tick).
  - **Monotonic:** the beat position only moves forward.
  - **Free-running:** when there's no audio to follow, it runs at the current tempo.
  - **First lock:** it jumps forward to the measured phase.
  - **Small corrections:** after that, it slews toward the tracker's phase with a 0.6 s time constant. The rate change is capped at ±25%, and tempo changes glide over 0.35 s.
  - **Large errors:** a phase error over 0.2 beat is ignored until it has persisted for 1.5 s, so a momentary off-beat misread does not drag the visuals.
  - **Unlocked:** it holds its last tempo and phase.
  - **Stalls:** after a stall longer than 1 s, it re-acquires the phase directly.
  - **Bar alignment:** `GetBarAlignedPosition` and `ResyncDownbeat` (the **B** key, or **Animation BPM → Resync Downbeat**) give tempo-synced video loops their bar-relative positions.
- **`MainWindow`** runs two clocks:
  - an **audio clock**, which follows the tracker and falls back to **Animation BPM** until the first lock;
  - a **manual clock**, which free-runs at **Animation BPM**. Changing the BPM slider no longer jumps animation phase.

  Both are reset when an offline render starts, so bakes are deterministic.

## Consumers

| Feature | Clock |
| --- | --- |
| Layer animations (Zoom, Translate, Rotate, Fade, DVD Bounce) | Audio clock when **Sync to Audio BPM** is on and an audio source is selected, otherwise manual clock |
| Beat Shake | Same as animations. Shakes start on clock beats; previously they started on raw onsets |
| FPS Modulation with **Sync to Audio BPM** | Audio clock (sine peaks on each beat) |
| Tempo-synced video loops (File/AutoClip **Sync to Beat**) | Same clock as animations, bar-aligned (`GetBarAlignedPosition`); **B** / **Resync Downbeat** sets bar beat 1. See [Tempo-Synced Video Loops](Tempo-Synced-Video.md) |
| Beat → Seeder, projectM "every N beats" | Still use raw onset counts (`AudioBeatDetector.BeatCount`), which react to actual hits |

## Time bases

Live tracker timestamps use `Stopwatch` seconds (`AudioBeatDetector.LiveAnalysisSeconds`), stamped at buffer arrival and back-dated per sample. Offline renders stamp samples on the render timeline, so tempo sync in bakes is driven by virtual time. Capture latency (roughly 10–20 ms for loopback) is not yet compensated.

## Validation

- `--smoke-test beat-tracking` covers synthetic material: four-on-the-floor at 100/124/128/140/174 BPM, 70 BPM half-time, a 174 BPM breakbeat, ±12 ms jitter, a 124→140 BPM change, a 4 s dropout, and manual-clock continuity. Phase error must stay under 10 ms; it is currently under 5 ms.
- `--smoke-test beat-tracking-file <audio or video path>` is a diagnostic for real music. It decodes up to 150 s with FFmpeg, logs tempo, confidence and lock every 5 s (`LIFEVIZ_BEAT_LOG_INTERVAL` overrides this), and summarizes first-lock time, tempo jumps and estimate jitter. It has no pass/fail threshold because there is no ground truth.
