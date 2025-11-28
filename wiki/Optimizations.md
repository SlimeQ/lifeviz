# Optimizations

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