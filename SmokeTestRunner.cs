using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace lifeviz;

internal static class SmokeTestRunner
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !string.Equals(args[0], "--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Logger.Initialize();
        App.SuppressErrorDialogs = true;
        App.IsSmokeTestMode = true;

        try
        {
            string target = args[1].Trim();
            exitCode = target.ToLowerInvariant() switch
            {
                "gpu-benchmark" => RunGpuBenchmark(),
                "gpu-handoff" => RunGpuCompositeToSimulationSmokeTest(),
                "gpu-sim" => RunGpuSimulationSmokeTest(),
                "gpu-source" => RunGpuSourceCompositeSmokeTest(),
                "gpu-render" => RunGpuPresentationSmokeTest(),
                "dimensions" => RunDimensionChangeSmokeTest(),
                "shutdown" => RunShutdownSmokeTest(),
                "startup" => RunStartupSmokeTest(),
                "all" => RunAllSmokeTests(),
                _ => throw new ArgumentException($"Unknown smoke test target '{target}'. Expected gpu-benchmark, gpu-handoff, gpu-sim, gpu-source, gpu-render, dimensions, shutdown, startup, or all.")
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Smoke test failed.", ex);
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }
        finally
        {
            Logger.Shutdown();
            App.SuppressErrorDialogs = false;
            App.IsSmokeTestMode = false;
        }

        return true;
    }

    private static int RunAllSmokeTests()
    {
        int gpuResult = RunGpuSimulationSmokeTest();
        if (gpuResult != 0)
        {
            return gpuResult;
        }

        return RunGpuUiSmokeSuite();
    }

    private static int RunGpuSimulationSmokeTest()
    {
        Logger.Info("Running GPU simulation smoke test.");
        using var backend = new GpuSimulationBackend();

        backend.Configure(144, 24, 16d / 9d);
        backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
        backend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
        backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);

        if (!backend.IsGpuAvailable || !backend.IsGpuActive)
        {
            throw new InvalidOperationException("GPU simulation backend did not activate for Naive Grayscale mode.");
        }

        bool[,] mask = BuildHalfPlaneMask(backend.Rows, backend.Columns);
        backend.InjectFrame(mask);
        for (int i = 0; i < 4; i++)
        {
            backend.Step();
        }

        byte[] fillBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(fillBuffer);
        ValidateColorBuffer(fillBuffer, "GPU grayscale fill");

        backend.SetBinningMode(GameOfLifeEngine.BinningMode.Binary);
        byte[] binaryBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(binaryBuffer);
        ValidateColorBuffer(binaryBuffer, "GPU grayscale binary");

        backend.SetMode(GameOfLifeEngine.LifeMode.RgbChannels);
        if (backend.IsGpuActive)
        {
            throw new InvalidOperationException("GPU simulation backend should fall back to CPU in RGB Channel Bins mode.");
        }

        var (r, g, b) = BuildRgbMasks(backend.Rows, backend.Columns);
        backend.InjectRgbFrame(r, g, b);
        backend.Step();
        byte[] rgbBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(rgbBuffer);
        ValidateColorBuffer(rgbBuffer, "CPU fallback RGB");

        backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);
        if (!backend.IsGpuActive)
        {
            throw new InvalidOperationException("GPU simulation backend did not reactivate after returning to Naive Grayscale.");
        }

        Logger.Info("GPU simulation smoke test passed.");
        return 0;
    }

    private static int RunGpuBenchmark()
    {
        Logger.Info("Running GPU benchmark.");

        const int simulationIterations = 180;
        using (var backend = new GpuSimulationBackend())
        {
            backend.Configure(144, 24, 16d / 9d);
            backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
            backend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
            backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);

            if (!backend.IsGpuAvailable || !backend.IsGpuActive)
            {
                throw new InvalidOperationException("GPU simulation backend did not activate for benchmark.");
            }

            bool[,] mask = BuildHalfPlaneMask(backend.Rows, backend.Columns);
            byte[] colorBuffer = new byte[backend.Columns * backend.Rows * 4];

            for (int i = 0; i < 12; i++)
            {
                backend.InjectFrame(mask);
                backend.Step();
                backend.FillColorBuffer(colorBuffer);
            }

            long injectTicks = 0;
            long stepTicks = 0;
            long fillTicks = 0;
            for (int i = 0; i < simulationIterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                backend.InjectFrame(mask);
                injectTicks += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                backend.Step();
                stepTicks += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                backend.FillColorBuffer(colorBuffer);
                fillTicks += Stopwatch.GetTimestamp() - start;
            }

            double tickScale = 1000.0 / Stopwatch.Frequency;
            Logger.Info(
                $"GPU sim benchmark: {backend.Columns}x{backend.Rows} depth {backend.Depth}, " +
                $"inject {injectTicks * tickScale / simulationIterations:0.###} ms, " +
                $"step {stepTicks * tickScale / simulationIterations:0.###} ms, " +
                $"fill/readback {fillTicks * tickScale / simulationIterations:0.###} ms.");
        }

        Exception? failure = null;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };

            const int compositeIterations = 180;
            var result = window.RunGpuSourceCompositeBenchmark(compositeIterations);
            if (!result.ok || result.buildCount <= 0)
            {
                failure ??= new InvalidOperationException("GPU source composite benchmark did not produce a valid composite.");
                app.Shutdown(1);
                return;
            }

            Logger.Info(
                $"GPU source benchmark: {result.width}x{result.height}, " +
                $"{result.buildCount} builds, {result.passCount} passes, " +
                $"upload {result.uploadMs / result.buildCount:0.###} ms, " +
                $"draw {result.drawMs / result.buildCount:0.###} ms, " +
                $"readback {result.readbackMs / result.buildCount:0.###} ms.");

            var handoff = window.RunGpuCompositeToSimulationBenchmark(compositeIterations);
            if (!handoff.ok || handoff.buildCount <= 0)
            {
                failure ??= new InvalidOperationException("GPU composite-to-simulation benchmark did not produce a valid handoff.");
                app.Shutdown(1);
                return;
            }

            Logger.Info(
                $"GPU handoff benchmark: {handoff.width}x{handoff.height}, " +
                $"{handoff.buildCount} builds, {handoff.passCount} passes, " +
                $"upload {handoff.uploadMs / handoff.buildCount:0.###} ms, " +
                $"draw {handoff.drawMs / handoff.buildCount:0.###} ms, " +
                $"readback {handoff.readbackMs / handoff.buildCount:0.###} ms, " +
                $"inject {handoff.injectMs:0.###} ms, " +
                $"step {handoff.stepMs:0.###} ms, " +
                $"fill/readback {handoff.fillMs:0.###} ms.");
            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU benchmark failed.", failure);
        }

        Logger.Info("GPU benchmark completed.");
        return exitCode;
    }

    private static int RunGpuSourceCompositeSmokeTest()
    {
        Logger.Info("Running GPU source composite smoke test.");
        Exception? failure = null;

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };
            bool ok = window.RunGpuSourceCompositeSmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU source compositor did not produce a valid composite through MainWindow.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU source composite smoke test failed.", failure);
        }

        Logger.Info("GPU source composite smoke test passed.");
        return exitCode;
    }

    private static int RunGpuCompositeToSimulationSmokeTest()
    {
        Logger.Info("Running GPU composite-to-simulation handoff smoke test.");
        Exception? failure = null;

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };
            bool ok = window.RunGpuCompositeToSimulationSmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU composite-to-simulation handoff did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU composite-to-simulation handoff smoke test failed.", failure);
        }

        Logger.Info("GPU composite-to-simulation handoff smoke test passed.");
        return exitCode;
    }

    private static int RunGpuPresentationSmokeTest()
    {
        Logger.Info("Running GPU presentation smoke test.");
        Exception? failure = null;
        GpuPresentationBackend.ResetSmokeCounters();

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };

            window.Loaded += (_, _) =>
            {
                int attempts = 0;
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                timer.Tick += (_, _) =>
                {
                    attempts++;

                    if (GpuPresentationBackend.CompositePipelineInitializationCount > 0)
                    {
                        timer.Stop();
                        window.Close();
                        app.Shutdown(0);
                        return;
                    }

                    if (attempts >= 20)
                    {
                        timer.Stop();
                        failure ??= new InvalidOperationException("GPU presentation backend did not initialize its composite pipeline through MainWindow.");
                        window.Close();
                        app.Shutdown(1);
                    }
                };
                timer.Start();
            };

            window.Show();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU presentation smoke test failed.", failure);
        }

        Logger.Info("GPU presentation smoke test passed.");
        return exitCode;
    }

    private static int RunDimensionChangeSmokeTest()
    {
        Logger.Info("Running dimension change smoke test.");
        Exception? failure = null;

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };

            window.Loaded += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var result = window.RunDimensionChangeSmoke(240, 24);
                    Logger.Info($"Dimension smoke result: configuredRows={result.configuredRows}, engineRows={result.engineRows}, engineColumns={result.engineColumns}, surface={result.surfaceWidth}x{result.surfaceHeight}, layerCount={result.layerCount}, allLayerRowsMatch={result.allLayerRowsMatch}, allLayerColumnsMatch={result.allLayerColumnsMatch}.");
                    if (result.configuredRows != 240 ||
                        result.engineRows != 240 ||
                        result.surfaceHeight != 240 ||
                        !result.allLayerRowsMatch ||
                        !result.allLayerColumnsMatch)
                    {
                        failure ??= new InvalidOperationException("Dimension change did not propagate to all simulation layers and the presentation surface.");
                        app.Shutdown(1);
                        return;
                    }

                    window.Close();
                    app.Shutdown(0);
                }), DispatcherPriority.ApplicationIdle);
            };

            window.Show();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Dimension change smoke test failed.", failure);
        }

        Logger.Info("Dimension change smoke test passed.");
        return exitCode;
    }

    private static int RunGpuUiSmokeSuite()
    {
        Logger.Info("Running GPU source + handoff + render smoke suite.");
        Exception? failure = null;
        GpuPresentationBackend.ResetSmokeCounters();

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };

            if (!window.RunGpuCompositeToSimulationSmoke())
            {
                failure ??= new InvalidOperationException("GPU composite-to-simulation handoff did not complete successfully through MainWindow.");
                app.Shutdown(1);
                return;
            }

            Logger.Info("GPU composite-to-simulation handoff smoke test passed.");

            if (!window.RunGpuSourceCompositeSmoke())
            {
                failure ??= new InvalidOperationException("GPU source compositor did not produce a valid composite through MainWindow.");
                app.Shutdown(1);
                return;
            }

            Logger.Info("GPU source composite smoke test passed.");

            window.Loaded += (_, _) =>
            {
                int attempts = 0;
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                timer.Tick += (_, _) =>
                {
                    attempts++;
                    if (GpuPresentationBackend.CompositePipelineInitializationCount > 0)
                    {
                        timer.Stop();
                        Logger.Info("GPU presentation smoke test passed.");
                        window.Close();
                        app.Shutdown(0);
                        return;
                    }

                    if (attempts >= 20)
                    {
                        timer.Stop();
                        failure ??= new InvalidOperationException("GPU presentation backend did not initialize its composite pipeline through MainWindow.");
                        window.Close();
                        app.Shutdown(1);
                    }
                };
                timer.Start();
            };

            window.Show();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU UI smoke suite failed.", failure);
        }

        return exitCode;
    }

    private static int RunStartupSmokeTest()
    {
        Logger.Info("Running startup smoke test.");
        Exception? failure = null;

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };

            window.Loaded += (_, _) =>
            {
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                {
                    Interval = TimeSpan.FromMilliseconds(900)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.Close();
                    app.Shutdown(0);
                };
                timer.Start();
            };

            window.Show();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Startup smoke test failed.", failure);
        }

        Logger.Info("Startup smoke test passed.");
        return exitCode;
    }

    private static int RunShutdownSmokeTest()
    {
        Logger.Info("Running shutdown smoke test.");
        Exception? failure = null;
        MainWindow? window = null;

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            app.Shutdown(1);
        };

        app.Startup += (_, _) =>
        {
            window = new MainWindow
            {
                Width = 160,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0.0
            };

            window.Loaded += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    window.OpenLayerEditor();
                    window.Close();
                    app.Shutdown(0);
                }), DispatcherPriority.ApplicationIdle);
            };

            window.Show();
        };

        int exitCode = app.Run();
        if (window?.GetShutdownErrorMessage() is string shutdownError && !string.IsNullOrWhiteSpace(shutdownError))
        {
            throw new InvalidOperationException($"Shutdown smoke test captured teardown error:\n{shutdownError}");
        }

        if (failure != null)
        {
            throw new InvalidOperationException("Shutdown smoke test failed.", failure);
        }

        Logger.Info("Shutdown smoke test passed.");
        return exitCode;
    }

    private static bool[,] BuildHalfPlaneMask(int rows, int columns)
    {
        var mask = new bool[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                mask[row, col] = col >= columns / 2;
            }
        }

        return mask;
    }

    private static (bool[,] r, bool[,] g, bool[,] b) BuildRgbMasks(int rows, int columns)
    {
        var red = new bool[rows, columns];
        var green = new bool[rows, columns];
        var blue = new bool[rows, columns];

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                if (col < columns / 3)
                {
                    red[row, col] = true;
                }
                else if (col < (2 * columns) / 3)
                {
                    green[row, col] = true;
                }
                else
                {
                    blue[row, col] = true;
                }
            }
        }

        return (red, green, blue);
    }

    private static void ValidateColorBuffer(byte[] buffer, string label)
    {
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException($"{label} produced an empty buffer.");
        }

        bool hasNonZero = buffer.Any(value => value != 0 && value != 255);
        bool hasVisible = buffer.Any(value => value != 0);
        if (!hasVisible)
        {
            throw new InvalidOperationException($"{label} produced an all-black buffer.");
        }

        if (!hasNonZero)
        {
            Logger.Warn($"{label} produced only binary extremes; continuing because output is still valid.");
        }
    }
}
