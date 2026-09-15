# Particle Erosion, Ripple Field, Chromatic Memory, and Contour Current

Select a **Sim Group** in the Scene Editor and choose the corresponding **Add** button. Each new layer defaults to Normal blend, full opacity, and audio mappings. For a direct view of the effect, also set the group's **Layer Blend** to **Normal**. Simulations in one group receive the same input; use separate successive Sim Groups to process one effect through another.

## Particle Erosion

Bright edges shed colored grains that drift through a changing current. Emission replenishes them from the scene; gravity pulls them down or up. The output is a field of grains with transparent space between them, so it can also be blended over another layer.

| Control | Range | Default |
| --- | --- | --- |
| Emission | 0–100% | 45% |
| Gravity | −100–100% | 45% |
| Turbulence | 0–100% | 55% |
| Persistence | 0–99% | 94% |

Negative Gravity lifts grains; zero removes the downward pull. Lower Persistence makes short-lived dust, while higher values create longer streams. Zero Emission stops replenishment and existing grains fade out. New mappings: **Low → Particle Emission** and **Mid → Particle Turbulence**, each with a 25% maximum additive amount.

The implementation transports colored grains on an image grid using nearest-neighbor sampling and sparse edge/brightness-driven emission. It is a visual granular effect, without individual particle objects or collision physics. Grains leave through open image boundaries. It reuses the full-resolution output history and allocates no extra simulation grid.

## Ripple Field

Scene changes disturb a damped wave surface. The waves propagate, intersect, and refract the incoming image. Sparse deterministic drips keep a still, lit image gently moving.

| Control | Range | Default |
| --- | --- | --- |
| Impulse | 0–100% | 55% |
| Wave Speed | 0–100% | 45% |
| Persistence | 90–99.9% | 98.5% |
| Refraction | 0–100% | 65% |

Impulse controls scene-driven disturbances, Wave Speed controls propagation, and Persistence controls damping. Refraction changes the visible distortion; zero returns the exact source image while the waves continue evolving. Try high Persistence with low Impulse for lingering interference. New mappings: **Low → Ripple Impulse** and **Mid → Ripple Refraction**, each at 25% maximum additive amount.

The GPU stores height, velocity, and previous source luminance in two float textures. Two stable wave substeps run per simulation step. The field preserves aspect and is capped at 360 rows and 262,144 pixels, bounding both float textures together at 8 MiB. Output uses the height gradient to refract the full-resolution scene with reflected edge sampling.

## Chromatic Memory

Red, green, and blue retain different amounts of the previous output, producing colored ghosts around moving subjects. Separation drifts those histories in different directions.

| Control | Range | Default |
| --- | --- | --- |
| Red Memory | 0–99% | 92% |
| Green Memory | 0–99% | 80% |
| Blue Memory | 0–99% | 65% |
| Separation | 0–100% | 35% |

Higher memory retains a channel longer. Set Separation to zero for color trails without spatial drift. Set all three memories to zero for exact passthrough. New mappings: **Low → Red Memory**, **Mid → Green Memory**, and **High → Blue Memory**, each at 7% maximum additive amount. Try this after Time Displacement in a separate Sim Group for layered spectral silhouettes.

Each channel samples the previous output with its own offset and retention. This uses a single previous-output image, not three frame-history rings. Alpha is the maximum of the interpolated channel coverages, so premultiplied color stays valid and transparent source regions can carry fading ghosts. The first frame starts from the current source.

## Contour Current

Image edges become luminous threads carried through a curling flow. Wider edge sampling creates thicker bands; retained contours form flowing paths.

| Control | Range | Default |
| --- | --- | --- |
| Flow | 0–100% | 50% |
| Thickness | 0–100% | 40% |
| Persistence | 0–99% | 94% |

Lower Thickness favors fine lines. Higher Flow stretches the trails into eddies; zero Flow holds their positions while the source refreshes. Zero Persistence shows only the current contours. New mappings: **Low → Contour Flow** and **Mid → Contour Thickness**, each at 25% maximum additive amount.

An adjustable-radius luminance gradient extracts scene contours. Source pigment is brightened within its alpha coverage and composited over bilinearly advected history. A smooth curl field and the local edge tangent direct that transport. It reuses existing full-resolution image textures with no auxiliary field allocation.

## Shared controls, saving, and timing

All four support the shared Opacity, Framerate, Hue Shift, and Hue Speed mapping outputs; their output dropdowns only add the type-specific mappings described above. All eight audio inputs remain available. Mappings normalize input through their Min/Max window, add input × amount to the authored value, and clamp to the control's range. No audio leaves the base values active. Choose a hardware Audio Source or **Video Stack (Silent)** in the context menu.

Live Mode updates immediately; draft mode waits for **Apply**. Autosaves, isolated clones, bake snapshots, and version 15 scene projects preserve all controls and mappings. Older supported projects still load. Randomize and resize clear simulation history; disabling holds state. Bakes start from fresh deterministic state and use a fixed frame clock with offline audio. Motion and persistence advance per simulation step, so simulation Framerate changes their apparent speed and trail duration.

All effects retain premultiplied alpha through GPU presentation and recording. Fading trails spend at least one alpha byte per step so rounding cannot leave permanent faint ghosts after input is removed. They create no decoder, media cache, or continuous disk writes. Cost scales with scene resolution and enabled layer count; Ripple additionally runs its bounded field passes. See [validation commands](Build-and-Install.md#validate-the-four-additional-simulations).
