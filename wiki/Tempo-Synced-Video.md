# Tempo-Synced Video Loops

File (video or GIF) and AutoClip layers can play their loops **locked to the beat** instead of in real time. Author a loop once at any tempo; LifeViz stretches or squeezes it to the music's tempo and keeps the loop's start on a bar line.

## Using it

1. **Tag the tempo the loops were made at.**
   - **Scene default:** right-click → **Animation BPM** → **Video loop BPM** (default 140, saved with the scene).
   - **Per layer:** in the Scene Editor's **Tempo Sync** group, **Loops authored at … BPM**. Leave it blank to use the scene default.
   - **Per AutoClip file:** select files in the AutoClip file list (Ctrl/Shift-click, or **Select All**), type a BPM next to **Loop BPM for selected**, and press **Tag Selected** or Enter. Tagged files show their BPM in the list. **Use Layer BPM** removes the tag from the selected files.
2. **Turn on Sync to Beat.** Use the layer's right-click menu, or the **Sync to beat** checkbox in the Tempo Sync group.
3. **Pick the clock.**
   - Synced loops follow the same [beat clock](Beat-Clock.md) as layer animations: **Animation BPM**, or the detected tempo when **Sync to Audio BPM** is on.
   - With Sync to Audio BPM off and Animation BPM equal to the loop BPM, a loop plays at its authored speed.
4. **Align bars.** Press **B** on a downbeat, or use **Animation BPM** → **Resync Downbeat**. That marks the nearest beat as beat 1, so bar-length loops and AutoClip switches start on the "1". The key is ignored while you type in a text box.

A loop's length in beats is its duration at the tagged tempo, rounded to whole beats. For example, at 140 BPM and 28 fps, 768 / 192 / 144 frames are 64 / 16 / 12 beats. The frame shown is `frac(barPosition / loopBeats) * frameCount`.

The tempo changes how fast the loop moves, never its length in beats:
- The loop plays faster at higher tempos and slower at lower ones.
- When the clock first locks, the loop jumps to the right phase.
- After that it follows the clock's smooth corrections.

### AutoClip with Sync to Beat

- **Frames:** each clip takes its frames from the beat clock, so the random start offset and **Loop selected file** are irrelevant: a synced clip is always its loop.
- **Bar-aligned timing:** clip lengths and delays still use the AutoClip ranges (in seconds), but every clip and delay ends on the bar line nearest its nominal end, and at least half a bar away. Successive clips therefore switch on downbeats.
- **Play whole file:** plays exactly one loop length.
- **Switching modes:** turning sync on or off restarts the AutoClip schedule. Per-file tag changes apply from the next clip.

### What changes on a synced layer

- **No audio:** synced loops play no audio, and the layer menu hides Play/Pause, Scrub, Play Audio and Audio Volume.
- **Restart:** on File layers, Restart is hidden too. AutoClip keeps **Restart Sequence**.
- **Status line:** the File layer menu shows the loop's beats, tagged BPM, and whether it is *cached*, *caching* or *streaming*.
- **Same file in several layers:** each synced File layer has its own player, so two layers on one file can be synced at different BPMs, or one synced and one playing normally. Decoded frames are still cached once per file, size and fit. While every layer on a file is synced, that file's real-time decoder is shut down.

## Frame sources: RAM cache or streaming

Right-click → **Performance** → **Loop Frame Cache** sets a RAM budget:
- **Off:** always stream.
- **Auto:** a quarter of physical RAM, at most 16 GiB.
- **Fixed:** 2/4/8/16/32 GB.

The menu shows current use. The setting is saved with the scene.

- **Cached:** a loop is decoded once, in the background, into BGRA frames at the layer's output size. Any frame can then be shown instantly, and no decoder runs afterwards.
  - Fills run at most two at a time. Fills for a loop that is playing now run before prefetches.
  - A synced AutoClip prefetches every file in its list, so after one pass through the cache nothing decodes at all.
  - Frames are usable as soon as they are decoded, even while the rest of the loop is still filling.
  - Least-recently-used loops are evicted to stay within the budget, and shrinking the budget evicts immediately.
  - A loop that would not fit is never cached.
- **Streaming:** without the cache, or when a loop doesn't fit, a forward-only decoder runs FFmpeg **without real-time pacing**. It only pulls a frame when the beat needs it, so the pipe's backpressure sets the decode speed.
  - **Starting mid-loop:** decoding begins with a seeked one-pass process for the rest of that loop iteration, and a looping process from frame 0 (spawned at the same time) takes over at the boundary. FFmpeg's `-stream_loop` wraps a seeked input back near the seek point rather than frame 0, so the two are never combined.
  - **When it restarts:** the decoder only restarts, with a frame-accurate seek, when the beat target jumps backwards (a downbeat resync) or falls more than about 0.75 s behind.
- **Background bakes** run in their own process, so they cache at most 2 GB to avoid doubling the editor's RAM use.

### Memory and decode budget, measured

- **Cache size:** the heavy-house set (nine 1080p VP9 alpha loops, 144–768 frames each at 28 fps) takes about 13.5 GiB at 1280×720 and fills within about 30 s in the background.
- **Streaming:** keeps up at 150 BPM for every file, but chladni-plate.webm (62 MB for 192 frames) only decodes at about 34 fps. That is just enough at normal tempos and too slow much above 160 BPM, so very high-bitrate loops are best cached. Use `--smoke-test tempo-sync-loops <folder>` to measure your own set.
- **Smaller machines (8 GB):** use **Off**, or a 1–2 GB budget, and keep loops short (1–4 bars) or the scene height low. The cache is sized at the layer's output resolution, not the file's.

## Offline renders

Exports drive the beat clock from the render timeline, and the clocks reset when a render starts, so synced loops are frame-exact and repeatable. Streamed exports wait for each exact frame. Cached exports wait for the sequential fill to reach it.

## Validation

- `--smoke-test tempo-sync` generates lossless loops whose pixel colour encodes the frame index. It checks:
  - exact frames, streamed and cached, direct and CPU-scaled, at 24 and 28 fps, across loop wraps and a backwards resync;
  - live following at 150 and 95 BPM (at most 2 frames behind, no restart churn);
  - cache accounting and eviction;
  - AutoClip switches landing on bar lines;
  - layer-config persistence.
- `--smoke-test tempo-sync-app` runs a File layer through a real `MainWindow`. It checks the window's beat-clock adapter, a downbeat resync, scene persistence, and switching sync off.
- `--smoke-test tempo-sync-loops <folder>` is a diagnostic over real loops: streaming versus cached, frames per second, longest hold, cache size and heap growth.
