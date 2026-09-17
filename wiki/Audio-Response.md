# Simulation audio response

Every simulation uses the same calibrated audio inputs and per-mapping response controls. Existing mappings keep their input, output, strength, and threshold settings, and gain a **5 ms Attack / 80 ms Release** default. Scene projects now use version 16; older supported scenes still load.

## Make an existing layer more responsive

Select the simulation in a Sim Group and click **Punchy mappings** in Reactive Mappings. This replaces that layer's mappings with a preset suited to its effect, keeping all base controls unchanged. It applies immediately in Live Mode; with Live Mode off, it remains a draft until **Apply**. New image-effect layers use these presets automatically. Life and Pixel Sort retain their empty mapping defaults; the button can add presets to either.

Presets use a narrower input window (8–85%) and choose a direction with room to move. For example, Chromatic Memory pulls its high retention values down on hits, so the scene snaps back into focus, then returns to colored trails between hits. Increasing an already-high retention value often reaches its ceiling too soon to show much variation.

| Simulation | Preset mappings |
| --- | --- |
| Life | Grayscale: Low → lower Threshold Min, Mid → Opacity; RGB: Low → lower Threshold Min, Mid → Hue Speed; Bitwise: Low → Opacity, Mid → Hue Speed |
| Pixel Sort | Low → Cell Width, Mid → Cell Height |
| Datamosh | Low → Feedback, Mid → Displacement |
| Fluid Ink | Low → Flow, Mid → Swirl (preserves Persistence) |
| Time Displacement | Low → Time Spread, Mid → Pattern Motion |
| Reaction–Diffusion | Low → Scene Seeding, Mid → Feed |
| Feedback Kaleidoscope | Low → Zoom, Mid → Twist |
| Particle Erosion | Low → Emission, Mid → Turbulence |
| Ripple Field | Low → Impulse, Mid → Refraction |
| Chromatic Memory | Low/Mid/High → Red/Green/Blue Memory (decrease) |
| Contour Current | Low → Flow, High → Persistence (decrease) |

For controls that can move in either direction, the preset uses 85% of the larger available distance to a limit. Presets keep the authored settings intact. They are starting points; use signed Strength and the input window to fit your material.

## Strength and response controls

- **Strength** accepts positive and negative values. Most outputs add normalized input × strength to their base value, then clamp to that control's supported range. Negative strength makes room for audible variation when a base value is near its upper limit. The displayed maximum depends on the output: pixels, degrees, zoom, or the effect's parameter range.
- **Attack ms** (0–150, default 5) controls how quickly the mapping follows a rising input. Keep it near 0–10 ms for sharp hits; larger values deliberately soften the onset.
- **Release ms** (0–500, default 80) controls how quickly it settles after a hit. Around 40–100 ms gives a short, smooth recovery. Longer values blend adjacent hits; zero tracks the input directly.
- **Input Min / Max** select the audio window that maps to 0–100%. Narrow it if the track only uses a small portion of the available input range. Thresholding occurs before the envelope, so crossing a narrow threshold does not bypass the response controls.

Opacity and Framerate retain their earlier positive-strength behavior: audio brings them toward their base value from a quieter floor. Negative strength lowers them on hits instead. Threshold Max also retains its earlier convention: positive strength lowers the upper threshold, and negative strength raises it. Pixel Sort cell dimensions and Kaleidoscope folds remain integer controls, so individual size changes remain discrete.

All eight audio inputs remain available. **Low / Mid / High** measure band power at 20–250 / 250–2,000 / 2,000–8,000 Hz. **Frequency** and its band variants now follow the energy-weighted geometric frequency center, reducing jumps between competing dominant FFT bins. Use level/band inputs for rhythmic strength and frequency inputs for timbre-driven movement.

All full-color field-effect sliders are available as mapping outputs, filtered to their owning type. This includes Fluid Persistence/Swirl, Time Pattern Scale/Motion, Reaction Feed/Kill, Kaleidoscope Folds/Center, Particle Gravity/Persistence, Ripple Wave Speed/Persistence, Chromatic Separation, and Contour Persistence.

## Timing and implementation

The previous spectrum analyzer selected a different FFT size from each capture packet and divided magnitude by each band's bin count. This made levels depend on packet size and understated wider mid/high bands. Analysis now uses a persistent 1,024-sample Hann window with a 120 Hz hop schedule and window-corrected summed band power. Equal-amplitude tones therefore produce comparable normalized levels across bands. Loudness uses the newest 10 ms of each packet rather than averaging a large backlog.

Windows output loopback requests event-driven 20 ms shared-mode capture; microphone AudioGraph requests the endpoint's lowest-latency quantum. Drivers determine the actual callback size. Video Stack (Silent) retains a bounded 100 ms analysis ring and consumes only its newest 2,048 samples (42.7 ms at 48 kHz) after a stall. This discards stale analysis samples without altering audible playback or exact offline decoding. If a live capture stops producing samples, mappings release toward zero after 100 ms rather than retaining the last value indefinitely.

Each mapping has one causal exponential envelope after input normalization, with time-based coefficients that are independent of presentation FPS. A single input snapshot is used across the simulation layers in each frame. Parameter edits preserve an envelope when its input and threshold window are unchanged; removing/replacing an input clears the corresponding state. Disconnecting audio, disabling a layer, or starting an offline timeline clears envelope state. Runtime envelopes are not serialized.

Fluid Flow has a nonlinear transport range with zero movement at zero and approximately unchanged default strength. Increasing Flow or changing Swirl also applies a curl impulse to the existing velocity field. The Fluid preset now drives both without reducing dye retention. Ripple Impulse changes excite a localized wave even on a still image. Reaction Seeding changes inject a chemical pulse once per parameter change rather than only altering a slow continuous source term. These respond to manual edits as well as audio; the simulations still evolve over time.

Synthetic 48 kHz checks measured an 11.7 ms bass onset through analysis and the default envelope, excluding capture, simulation cadence, and display latency. One tested Windows output endpoint delivered 10 ms packets. These are measured checks, not an end-to-end latency guarantee. Slow simulation FPS, high persistence, and expensive scenes can still make an effect feel slower. See [Build & Install](Build-and-Install.md#validate-simulation-audio-response) for the tests.

## Reading the live response

Each mapping displays raw Input, normalized/smoothed Response, and its final Live output after all mappings and clamping. If Input stays at Max, lower capture gain or raise Input Max to restore headroom. If Input moves but Response stays zero, lower Input Min. If Response moves but Live output stays pinned, reduce Strength or move the base setting away from its limit. Draft rows require Apply. Missing samples and disabled or incompatible mappings get an explicit status.

Opacity measures contribution, not brightness. With Normal blending, +100% strength maps silence to transparent and full input to base Opacity; −100% reverses it. The lower scene remains visible through the simulation. Subtractive blending darkens as the effect contribution increases. Multiple opacity mappings multiply, and group opacity also affects the final result.

Life thresholds select new input, not existing cells. Injection Dropout (formerly Noise) discards that input; 100% does not clear history. Bitwise uses source RGB bits directly, so threshold/injection-mode/binning controls do not apply. Grayscale hue mappings do not apply. The editor filters these choices and retains older incompatible mappings with a status message. Hue Speed now advances continuously when its rate changes.
