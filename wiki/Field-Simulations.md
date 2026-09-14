# Fluid Ink, Time Displacement, and Reaction–Diffusion

These are independent GPU simulation layer types, alongside Life Sim, Pixel Sort, and Datamosh. They process the composited scene at their Sim Group's position and have their own state, blend, opacity, hue, and audio mappings.

## Try them

1. Add a video, image, webcam, window, or Color Plane, followed by a **Sim Group**.
2. Select that group in the Scene Editor and click **Add Fluid Ink**, **Add Time Displacement**, or **Add Reaction–Diffusion**.
3. Disable the default Life Sim to audition one effect. Set the group's **Layer Blend** to **Normal** and **Layer Opacity** to 100% to see its processed image directly.
4. Select the simulation to adjust its controls. New effects start with Normal blending, 100% simulation opacity, and one Low audio mapping at 30% amount.
5. Choose **Audio Source** in the context menu to use hardware audio or **Video Stack (Silent)**. The latter analyzes video layers with Play Audio enabled. Without audio, the authored base values still work.

All simulations within one group read the same input; their outputs blend in list order. Use separate Sim Groups to feed one effect's result into another. Live Mode applies changes immediately; with Live Mode off, press Apply. Save/Load, autosave, editor drafts, and bake snapshots preserve settings independently. Exported projects use version 13; older supported projects still load.

## Fluid Ink

Scene colors feed a persistent image transported by an evolving, pressure-corrected velocity field. Image brightness gradients stir the field, and a smooth changing swirl moves flat areas too.

| Control | Range / default | Effect |
| --- | --- | --- |
| Flow | 0–100% / 45% | Strength of new forces. Setting it to zero lets existing velocity decay. |
| Persistence | 0–99.5% / 94% | Retained dye versus fresh scene each simulation step. Zero returns exact current input. |
| Swirl | 0–100% / 45% | Additional broad curling forces, scaled by Flow. |

The default **Low → Fluid Flow** mapping adds up to 30 percentage points. A moving, colorful subject at 90–97% persistence is a useful starting point. High persistence can soften or obscure the source.

## Time Displacement

A moving interference pattern selects different ages of the source across the image. Moving subjects stretch into delayed silhouettes; a stationary source remains stationary even as the delay pattern moves.

| Control | Range / default | Effect |
| --- | --- | --- |
| Time Spread | 0–100% / 65% | Maximum age as a fraction of available history. Zero returns exact full-resolution input. |
| Pattern Scale | 0.5–12 / 3 | Number of spatial wave cycles across the image. |
| Pattern Motion | 0–100% / 30% | Speed at which the spatial delay pattern changes. Zero holds the pattern, while history still updates. |

The default **Low → Time Spread** mapping adds up to 30 percentage points. Each layer retains 64 simulation-step samples, including the latest: the oldest is 63 steps behind (2.1 seconds at 30 simulation FPS, 1.05 seconds at 60). History fills gradually after startup/reset; missing history is never sampled. Delayed frames use a reduced-resolution buffer with bilinear spatial and temporal interpolation, so high-resolution delayed regions can look softer. The current-frame endpoint and zero-spread bypass retain full resolution.

## Reaction–Diffusion

Two chemical concentrations diffuse and react using a Gray–Scott model. The source image seeds small colonies; Feed and Kill change their growth into spots, islands, or connected patterns. Scene colors tint the result, and colony boundaries receive a blue highlight. Transparent source pixels remain transparent.

| Control | Range / default | Effect |
| --- | --- | --- |
| Feed | 0.0100–0.0800 / 0.0367 | Replenishment of the first chemical. |
| Kill | 0.0300–0.0750 / 0.0649 | Removal of the second chemical, in addition to Feed. |
| Scene Seeding | 0–100% / 35% | Initial colony strength and sparse ongoing source-driven injection. Zero stops new injection; established chemistry keeps evolving. |

The default **Low → Reaction Seeding** mapping adds up to 30 percentage points. Give patterns several seconds to develop. Feed/Kill combinations can settle into uniform output or extinguish the colonies; this is part of the model. Raise Seeding and use Randomize to start again. A bright still image or Color Plane can sustain growth without moving video.

## State, timing, and performance

**Randomize** clears these effects' history and fields and also randomizes other simulations. Disable holds the state; re-enable resumes it. Changing dimensions or recreating/loading the layer starts fresh state. Saved scenes store parameters, not live history or chemicals. Changing simulation FPS changes the real-time duration/speed of all three effects. The fixed simulation clock and offline video-audio analysis drive bakes from a fresh timeline.

The flow/chemical grids and time-history frames are capped at 360 rows and 262,144 pixels, maintaining scene aspect. Time Displacement's history is bounded to 64 MiB per layer. Fluid Ink and Reaction–Diffusion use two floating-point field textures, at most 8 MiB combined, plus the shared full-resolution input/output textures. Fluid Ink performs 15 field passes plus one output pass per step; Reaction–Diffusion performs eight chemical substeps plus one output pass. Time Displacement captures one reduced-resolution frame and resolves one output pass. Full-resolution output textures and additional layers still increase GPU memory and bandwidth use. These are GPU effects, with CPU readback for recording/fallback composition, rather than CPU simulation implementations.

See [Rendering Pipeline](Rendering-Pipeline.md) for implementation details and [Build & Install](Build-and-Install.md#validate-field-simulations) for validation.
