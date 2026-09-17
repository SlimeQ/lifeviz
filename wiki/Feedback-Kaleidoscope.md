# Feedback Kaleidoscope

Select a **Sim Group** in the Scene Editor and choose **Add Feedback Kaleidoscope**. It folds the incoming scene into mirrored wedges and feeds back its previous output with zoom and twist, making repeating tunnels and rotating trails. It occupies the same simulation slot as Life, Fluid Ink, and Datamosh.

The new simulation defaults to Normal blend and full opacity. Set the group's **Layer Blend** to **Normal** to see the processed image directly. Simulations within one group read the same incoming scene; to process Fluid Ink through Kaleidoscope, put them in two successive Sim Groups.

| Control | Range | Default | Behavior |
| --- | --- | --- | --- |
| Feedback | 0–98% | 82% | Retained output mixed with the newly folded scene. Zero keeps the kaleidoscope geometry and removes trails. |
| Zoom / step | 0.900–1.100 | 1.015 | Above 1 expands retained imagery; below 1 contracts it. |
| Twist °/step | −10–10 | 1 | Rotates retained imagery each simulation step. Negative values reverse direction. |
| Folds | 2–16 | 6 | Number of repeated mirrored sectors. |
| Center X / Y | 0–100% | 50% / 50% | Moves the fold center across the canvas. |

New layers map **Low → Zoom** and **Mid → Twist** using the [Punchy mappings preset](Audio-Response.md), with signed strength and fast-attack/smooth-release controls. All six effect controls are available as mapping outputs. Choose an Audio Source in the context menu, including Video Stack (Silent) for analysis without speaker output. No audio leaves the base settings active.

For a calmer effect, remove the two default mappings and set Twist to zero. Increase Feedback for longer trails; lower it for a clearer source image. Zoom and Twist are per simulation step, so changing simulation Framerate changes their speed in seconds.

Live Mode applies edits immediately; draft mode waits for **Apply**. Settings and mappings survive autosaves, cloning, and version 16 scene projects. **Randomize** clears retained imagery; disabling the layer holds its state. Resizing and background bakes start with fresh history.

The GPU folds in pixel coordinates so wide and tall scenes retain the same geometry. Bilinear sampling smooths transformed pixels, reflected edges avoid clamped edge smears, and premultiplied alpha preserves transparency. One full-resolution output pass reuses the existing image ping-pong textures; it allocates no extra field grid or temporal ring and creates no decoder or disk cache. See [Rendering Pipeline](Rendering-Pipeline.md) and [validation commands](Build-and-Install.md#validate-feedback-kaleidoscope-and-mapping-filters).
