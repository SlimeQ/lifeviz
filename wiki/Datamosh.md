# Audio-reactive Datamosh

Datamosh creates temporal smears and broken-looking image blocks from live scene frames. It simulates the appearance of datamoshing with GPU feedback; it does not damage video files or manipulate codec motion vectors.

## Try it

1. Put a moving video, webcam, or window source in the scene and add/select a **Sim Group** after it in processing order.
2. In the group's simulation stack, click **Add Datamosh Layer**. Remove/disable the default Life Sim if you want the effect directly on the source image. Place Datamosh after another simulation to smear that simulation's output.
3. Select the new Datamosh layer. Its default Normal blend replaces its incoming image with the processed image; use opacity to mix it with the incoming image.
4. Right-click the preview and select **Audio Source**. Choose a microphone/input, a loopback output, or **Video Stack (Silent)**. For silent video-stack analysis, enable **Play Audio** on the video layers that should contribute.
5. Play the source. New Datamosh layers have **Low → Datamosh Feedback** (80%) and **Mid → Datamosh Displacement** (60%) mappings. Bass increases frame retention and mids push retained blocks around. Change either mapping's input or thresholds to tune it to your music.

## Controls

| Control | Range | Behavior |
| --- | --- | --- |
| Feedback | 0–98% | Retains older pixels; 0 returns clean input on the next step. Default 15%. |
| Displacement | 0–100% | Moves retained blocks each step, up to 8% of the shorter image dimension. Default 0%. Requires feedback. |
| Block Size | 1–512 px | Square patches at simulation resolution, default 16 px. Larger blocks produce broader tears. |
| Datamosh Feedback mapping | 0–100% amount | Adds audio input × amount to base feedback, capped at 98%. |
| Datamosh Displacement mapping | 0–100% amount | Adds audio input × amount to base displacement, capped at 100%. |

For stronger trails, raise base Feedback to 50–70%. For a mostly clean picture between loud passages, keep the base values low and raise the mappings' lower input thresholds. Removing the mappings leaves a manual effect. With no selected audio input, base settings still apply.

Use the existing **Randomize** menu item to clear retained frames; this also randomizes other simulations. Disabling an effect holds its state, and re-enabling resumes it. Changing dimensions or reloading/recreating the layer starts a fresh history. The effect runs at simulation FPS, so a different simulation rate changes how long trails persist in real time.

Autosaves, editor drafts, and version 12 scene projects preserve settings and mappings. Existing project versions continue to load. Fixed-duration bakes use their normal video-audio analysis and start a fresh effect timeline; the live preview's retained pixels are not part of the saved scene. A bake with only unavailable live audio uses the authored base settings.

See [Configuration & Controls](Configuration-and-Controls.md), [Rendering Pipeline](Rendering-Pipeline.md), and [validation commands](Build-and-Install.md#validate-datamosh).
