# Optimizations

## Runtime contention and disk-I/O pass

The July 2026 performance pass targeted work that continued even when it could not improve the visible frame, as well as synchronous disk activity on the UI thread.

- Configuration saves are debounced for 500 ms, serialized once per settled revision, and written by one background worker through a same-directory temporary file. Identical JSON is skipped, in-flight and pending revisions are tracked separately so an `A -> B -> A` edit cannot persist stale `B`, and shutdown drains the last revision. This replaces full, synchronous `%AppData%\lifeviz\config.json` rewrites from slider and text-change events.
- Session logging is nonblocking for callers. A bounded 512-record queue feeds one below-normal writer, batches output, flushes no more than every 500 ms, and caps each launch at 8 MiB. The offline audio decoder also resets PCM timestamps, preventing repeated non-monotonic timestamp warnings from flooding that queue.
- File metadata probing is lazy and process-wide. Direct videos also initialize asynchronously in a pending state. Sequence, AutoClip, and direct layers share at most two `ffmpeg` probes, with one 15-second deadline covering both the queue and subprocess. The cache is capped at 256 tasks, local successes include file size/write time in their key, and failures retry after a 10-second backoff. Offline sequence/AutoClip handoffs share one deadline across paths and later frames; an exhausted source is skipped for that export rather than multiplying the timeout by the library size.
- Disabled sources are rejected before capture and before both compositors. Disabled file, sequence, and AutoClip decoders suspend without overwriting explicit pause state; window and webcam sessions are released. A disabled parent group makes every descendant effectively disabled, including media added later or resolved asynchronously.
- The GPU source compositor retains an LRU cache of D3D11 upload texture/SRV pairs keyed by source dimensions, capped at eight entries and 64 MiB (or one unusually large active entry). Mixed-resolution stacks can alternate dimensions without destroying and recreating the same resources each frame or retaining eight 4K allocations.
- Window capture is paced to approximately 30 fps rather than polling `PrintWindow`/BitBlt nearly continuously. Removal cancels immediately and defers GDI resource disposal until an in-progress capture actually exits, avoiding a UI-thread wait and teardown race.
- Normal rendering no longer builds profiler metric names, freshness summaries, or timing stamps unless profiling or **Show FPS** needs them. Source mapping coefficients are reused while dimensions are unchanged.
- **Show FPS** refreshes its text and rolling gap summary at 6 Hz while its graphs keep their independent capped cadences. FFT storage and waveform-history scratch storage are reused instead of allocated on each audio quantum.

The `config-save-coalescing` smoke test exercises burst, duplicate, changed, and shutdown persistence behavior. `gpu-source` covers the D3D source compositor, while the current-scene profiling targets measure the complete saved stack.

The application's core logic has been significantly optimized to improve performance, especially on multi-core systems. Several key parts of the application that perform heavy computations over large collections of data now run in parallel. This allows for larger grid sizes, more complex scenes with multiple sources, and higher frame rates.

The following components have been optimized by converting their sequential `for` loops into parallel operations using `System.Threading.Tasks.Parallel.For`, which distributes the workload across available CPU cores.

### Core Simulation (`GameOfLifeEngine.cs`)

-   **`StepChannel`:** The primary `Step` function, which calculates the next generation for every cell in the simulation, has been parallelized. The calculation for each row of the grid is now an independent task, allowing the engine to scale with the number of available cores.

### Rendering & Compositing Pipeline (`MainWindow.xaml.cs`)

The main rendering and compositing pipeline involves several steps that iterate over large pixel buffers. These have been parallelized:

-   **Source Compositing (`CompositeIntoBuffer`, `CopyIntoBuffer`):** When multiple video or webcam sources are active, the process of resizing and blending them into a single composite image is now performed in parallel. Each row of the destination buffer is processed independently.

-   **Color Buffer Generation (`EnsureEngineColorBuffer`):** The process of converting the raw `(true/false)` state from the simulation engine into a displayable color buffer has been parallelized.

-   **Injection Mask Generation (`BuildLuminanceMask`, `BuildChannelMasks`):** The methods responsible for creating boolean masks from the composited input sources are now parallel. These masks determine how external video feeds influence the Game of Life simulation.

-   **Final Render & Effects (`RenderFrame`, `InvertBuffer`):**
    -   The final step of copying the computed simulation state into the buffer that is displayed on the screen has been parallelized.
    -   The `InvertBuffer` effect, which inverts the colors of a buffer, has also been updated to run in parallel.
