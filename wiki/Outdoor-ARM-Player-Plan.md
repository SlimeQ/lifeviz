# Outdoor ARM Player Plan

Status: proposed design, not an implemented player or validated hardware build. Updated September 12, 2026.

## Requirements

- Under $300 total hardware budget, including power and weather protection.
- Fits in a car; no laptop at the installation.
- Summer woodland use, four nights of 8–12 hours; size for 48 operating hours without assuming solar or recharging.
- Project onto a white sheet/screen 6–10 feet wide; 480p is acceptable.
- Waterproof the operating equipment, including the projector, connections, and audio input. A tarp alone does not satisfy this requirement.
- Author on the existing desktop editor, put a prepared project and its assets on a drive, and boot directly into playback.
- Preserve live simulation and audio reactivity. A prerecorded movie is only an optional fallback, not an equivalent replacement.

## Proposed platform

Start with a Raspberry Pi 4 running 64-bit Linux and a dedicated fullscreen player. Treat memory size and performance as benchmark decisions. The Pi 4 has OpenGL ES 3.1 support; a Pi Zero 2 W is a later optimization target, not an assumed compatible substitute for the GPU implementation.

Keep the playback core separate from the platform host so Android can be considered later. Android hardware must prove app installation, offline automatic startup, GPU features, USB storage and microphone access, and recovery after loss of power. An Android projector could remove an external board, but only after these checks on a specific device.

## What the repository can supply

- `LayerConfigFile.cs`: versioned JSON scene structure and migration knowledge. Current export is JSON, not an asset bundle, and conversion methods depend on editor models.
- `GameOfLifeEngine.cs` and `SimulationReactivity.cs`: candidates for extracting platform-independent logic and reference behavior. Verify dependencies before sharing them directly.
- `SimulationBackend.cs`: a starting abstraction; GPU extensions currently expose Windows-specific surfaces and handles and need redesign.
- `GpuSimulationBackend.cs`, compositor backends, and HLSL shaders: behavior to reproduce with a Linux graphics backend, not binaries that can run unchanged.
- `AudioBeatDetector.cs`: analysis algorithms to separate from WinRT/WASAPI capture.
- `FileCaptureService.cs`: media scheduling/reference behavior, currently mixed with Windows imaging and audio dependencies.

This is a player implementation and extraction effort, not just an ARM build flag.

## Player package and operation

Proposed desktop action: **Export for Player**. This control does not exist yet.

1. Validate the scene against a versioned target capability profile before export. Report unsupported layers and settings rather than silently changing the scene.
2. Package a manifest, scene JSON, and all referenced assets with portable relative paths. Validate paths, file hashes, versions, and required capabilities on import.
3. Optionally prepare target-friendly media. Bake static/nonreactive subtrees only where their timing and compositing are preserved; keep reactive simulation live. Preserve transparency when required.
4. At boot, discover a package in a documented drive location, validate it, and install atomically to internal storage. Keep a last-known-good package if import is incomplete or invalid.
5. Start fullscreen without an editor, network, account, keyboard, or login prompt. Recover after a crash, audio-device reconnect, and power interruption.
6. Keep system writes bounded; use a read-only base filesystem with a separate writable package/state area. The import drive can be removed after a successful install indication.

## Initial implementation sequence

1. **Hardware feasibility:** borrow candidate hardware if possible; measure projector brightness at six and ten feet, full-system energy, and sealed enclosure temperature before committing purchases.
2. **Portable contract:** extract scene data and validation without WPF types; define capabilities and deterministic reference fixtures.
3. **Vertical slice:** boot Linux ARM into one representative scene with one source, one live Life Sim, microphone reactivity, and 480p output at a target 30 fps. Test actual GPU performance and power.
4. **Required scene coverage:** add the sources, blend modes, animations, and playlist behavior used by the intended woods project. Support other features incrementally and reject them explicitly until implemented.
5. **Appliance workflow:** desktop package export, offline import, startup service, restart recovery, last-known-good package, and simple status indication.
6. **Qualification:** compare supported rendering/audio behavior with desktop fixtures; test missing assets and interrupted imports; run four complete nights on the chosen battery and enclosure.

## Power feasibility

Budget from measured battery-terminal watts, including regulators, audio, and cooling. With 80% of nominal energy available as a planning allowance:

| Whole-system average at battery | Nominal battery for 32 hours | Nominal battery for 48 hours |
| --- | ---: | ---: |
| 15 W | 600 Wh | 900 Wh |
| 20 W | 800 Wh | 1,200 Wh |
| 30 W | 1,200 Wh | 1,800 Wh |
| 60 W | 2,400 Wh | 3,600 Wh |

A 12.8 V, 100 Ah battery has 1,280 Wh nominal. At the stated allowance, the 48-hour average limit is about 21.3 W. This is an energy constraint, not a claim that a suitable projector and player can achieve it. A six-to-ten-foot image, waterproof cooling, and an all-in $300 budget have not been shown feasible together.

Prefer regulated DC outputs matched to each device to avoid unnecessary AC conversion. A nominal 12 V battery must not be connected directly to an arbitrary nominal 12 V input: confirm the entire battery voltage range, connector, polarity, current, and device tolerance. Include a compatible charger, fuse close to the battery, protected terminals, wiring, and converters in the cost and energy tests. Do not use the vehicle starter battery as the installation battery.

## Waterproofing concept and acceptance

Use separate battery and optical/electronics compartments as appropriate. Plan gasketed closures, sealed cable entries, a clear optical window, and an acoustic membrane or appropriately protected external microphone. A windscreen alone is not waterproofing.

The projector needs an engineered heat path to the exterior. A sealed plastic box with an internal fan merely circulates heat. Evaluate a conductive enclosure/heat exchanger or another verified weatherproof cooling design while preserving the projector's required internal airflow. No specific inexpensive enclosure has yet been qualified.

Validate splash ingress with equipment unpowered, then thermal operation under controlled dry conditions, and then supervised weather exposure within component limits. Test dew and condensation, lens/window fogging, summer daytime storage temperatures, and an over-temperature shutdown. A modified enclosure does not inherit an IP rating from its unmodified box or individual glands.

Keep the 6–10-foot screen secured for wind and define conditions for taking it down. Waterproof electronics do not make the screen stormproof.

## Decisions and purchasing gates

- $300 remains the total cap; no qualified shopping list exists yet.
- First prove acceptable brightness with a low-power projector. Do not promise the earlier small-projector energy target for the requested large image.
- Benchmark a Pi 4 player before choosing a smaller board or promising frame rate.
- Count every accessory and the charger in the budget. Borrowed/used equipment may be necessary; borrowing, shorter runtime, smaller images, or recharging are options requiring an explicit choice, not assumptions.
- Do not spend the full budget on a port-dependent installation before the optical, energy, and waterproof thermal tests pass.

## References

- [Raspberry Pi 4 specifications](https://www.raspberrypi.com/products/raspberry-pi-4-model-b/specifications/)
- [Raspberry Pi Zero 2 W product brief](https://datasheets.raspberrypi.com/rpizero2/raspberry-pi-zero-2-w-product-brief.pdf)
- [Scene Saving & Recovery](Scene-Saving-and-Recovery.md)
- [Rendering Pipeline](Rendering-Pipeline.md)

Implementation must update the README and the relevant rendering, controls, and build/install wiki pages as each feature ships.
