using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
            string? smokeVideoPath = args.Length >= 3 ? args[2] : Environment.GetEnvironmentVariable("LIFEVIZ_SMOKE_VIDEO");
            exitCode = target.ToLowerInvariant() switch
            {
                "profile-240" => RunFrameProfileSmokeTest(240, "smoke-mainloop-240p"),
                "profile-480" => RunFrameProfileSmokeTest(480, "smoke-mainloop-480p"),
                "profile-rgb-240" => RunFrameProfileSmokeTest(240, "smoke-mainloop-rgb-240p", rgbMode: true),
                "profile-rgb-480" => RunFrameProfileSmokeTest(480, "smoke-mainloop-rgb-480p", rgbMode: true),
                "profile-file-240" => RunFrameProfileSmokeTest(240, "smoke-mainloop-file-240p", rgbMode: false, smokeVideoPath),
                "profile-file-480" => RunFrameProfileSmokeTest(480, "smoke-mainloop-file-480p", rgbMode: false, smokeVideoPath),
                "profile-file-rgb-240" => RunFrameProfileSmokeTest(240, "smoke-mainloop-file-rgb-240p", rgbMode: true, smokeVideoPath),
                "profile-file-rgb-480" => RunFrameProfileSmokeTest(480, "smoke-mainloop-file-rgb-480p", rgbMode: true, smokeVideoPath),
                "profile-current-scene" => RunCurrentSceneProfileSmokeTest(visibleWindow: false),
                "profile-current-scene-visible" => RunCurrentSceneProfileSmokeTest(visibleWindow: true),
                "profile-current-scene-interaction" => RunCurrentSceneInteractionProfileSmokeTest(),
                "gpu-benchmark" => RunGpuBenchmark(),
                "gpu-handoff" => RunGpuCompositeToSimulationSmokeTest(),
                "gpu-rgb-threshold" => RunGpuCompositeRgbThresholdSmokeTest(),
                "gpu-frequency-hue" => RunGpuFrequencyHueSmokeTest(),
                "gpu-injection-mode" => RunGpuInjectionModeSmokeTest(),
                "gpu-file-injection-mode" => RunGpuFileInjectionModeSmokeTest(smokeVideoPath),
                "gpu-sim" => RunGpuSimulationSmokeTest(),
                "gpu-source" => RunGpuSourceCompositeSmokeTest(),
                "source-reset" => RunSourceResetSmokeTest(),
                "gpu-render" => RunGpuPresentationSmokeTest(),
                "profile-mainloop" => RunFrameProfileSmokeTest(),
                "dimensions" => RunDimensionChangeSmokeTest(),
                "shutdown" => RunShutdownSmokeTest(),
                "startup" => RunStartupSmokeTest(),
                "all" => RunAllSmokeTests(),
                _ => throw new ArgumentException($"Unknown smoke test target '{target}'. Expected profile-240, profile-480, profile-rgb-240, profile-rgb-480, profile-file-240, profile-file-480, profile-file-rgb-240, profile-file-rgb-480, profile-current-scene, profile-current-scene-visible, profile-current-scene-interaction, gpu-benchmark, gpu-handoff, gpu-rgb-threshold, gpu-frequency-hue, gpu-injection-mode, gpu-file-injection-mode, gpu-sim, gpu-source, source-reset, gpu-render, profile-mainloop, dimensions, shutdown, startup, or all.")
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
            App.LoadUserConfigInSmokeTest = false;
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
        if (!backend.IsGpuActive)
        {
            throw new InvalidOperationException("GPU simulation backend did not stay active for RGB Channel Bins mode.");
        }

        var (r, g, b) = BuildRgbMasks(backend.Rows, backend.Columns);
        backend.InjectRgbFrame(r, g, b);
        backend.Step();
        byte[] rgbBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(rgbBuffer);
        ValidateColorBuffer(rgbBuffer, "GPU RGB");

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

    private static int RunSourceResetSmokeTest()
    {
        Logger.Info("Running source reset smoke test.");
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
                    bool ok = window.RunSourceResetSmoke();
                    if (!ok)
                    {
                        failure ??= new InvalidOperationException("Source reset path did not preserve visible passthrough output.");
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
            throw new InvalidOperationException("Source reset smoke test failed.", failure);
        }

        Logger.Info("Source reset smoke test passed.");
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

    private static int RunGpuCompositeRgbThresholdSmokeTest()
    {
        Logger.Info("Running GPU RGB threshold smoke test.");
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
            bool ok = window.RunGpuCompositeRgbThresholdSmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU RGB threshold smoke did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU RGB threshold smoke test failed.", failure);
        }

        Logger.Info("GPU RGB threshold smoke test passed.");
        return exitCode;
    }

    private static int RunGpuInjectionModeSmokeTest()
    {
        Logger.Info("Running GPU injection-mode smoke test.");
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
            bool ok = window.RunGpuInjectionModeSmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU injection-mode smoke did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU injection-mode smoke test failed.", failure);
        }

        Logger.Info("GPU injection-mode smoke test passed.");
        return exitCode;
    }

    private static int RunGpuFrequencyHueSmokeTest()
    {
        Logger.Info("Running GPU frequency-hue smoke test.");
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
            bool ok = window.RunGpuFrequencyHueSmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU frequency-hue smoke did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU frequency-hue smoke test failed.", failure);
        }

        Logger.Info("GPU frequency-hue smoke test passed.");
        return exitCode;
    }

    private static int RunGpuFileInjectionModeSmokeTest(string? smokeVideoPath)
    {
        if (string.IsNullOrWhiteSpace(smokeVideoPath))
        {
            throw new ArgumentException("gpu-file-injection-mode requires a video path as the third argument or LIFEVIZ_SMOKE_VIDEO.");
        }

        Logger.Info("Running GPU file injection-mode smoke test.");
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
            bool ok = window.RunGpuFileInjectionModeSmoke(smokeVideoPath);
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU file injection-mode smoke did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU file injection-mode smoke test failed.", failure);
        }

        Logger.Info("GPU file injection-mode smoke test passed.");
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
                    window.SetReferenceSimulationLayerLifeModeForSmoke(GameOfLifeEngine.LifeMode.RgbChannels);
                    var directResult = window.RunDimensionChangeSmoke(240, 24);
                    Logger.Info($"Dimension smoke direct result: configuredRows={directResult.configuredRows}, engineRows={directResult.engineRows}, engineColumns={directResult.engineColumns}, surface={directResult.surfaceWidth}x{directResult.surfaceHeight}, layerCount={directResult.layerCount}, allLayerRowsMatch={directResult.allLayerRowsMatch}, allLayerColumnsMatch={directResult.allLayerColumnsMatch}.");
                    if (directResult.configuredRows != 240 ||
                        directResult.engineRows != 240 ||
                        directResult.surfaceHeight != 240 ||
                        !directResult.allLayerRowsMatch ||
                        !directResult.allLayerColumnsMatch)
                    {
                        failure ??= new InvalidOperationException("Direct dimension change did not propagate to all simulation layers and the presentation surface.");
                        app.Shutdown(1);
                        return;
                    }

                    var editorLiveResult = window.RunSceneEditorDimensionSmoke(480, liveMode: true);
                    Logger.Info($"Dimension smoke scene editor live result: configuredRows={editorLiveResult.configuredRows}, engineRows={editorLiveResult.engineRows}, engineColumns={editorLiveResult.engineColumns}, surface={editorLiveResult.surfaceWidth}x{editorLiveResult.surfaceHeight}, layerCount={editorLiveResult.layerCount}, allLayerRowsMatch={editorLiveResult.allLayerRowsMatch}, allLayerColumnsMatch={editorLiveResult.allLayerColumnsMatch}.");
                    if (editorLiveResult.configuredRows != 480 ||
                        editorLiveResult.engineRows != 480 ||
                        editorLiveResult.surfaceHeight != 480 ||
                        !editorLiveResult.allLayerRowsMatch ||
                        !editorLiveResult.allLayerColumnsMatch)
                    {
                        failure ??= new InvalidOperationException("Scene Editor live dimension change did not propagate to all simulation layers and the presentation surface.");
                        app.Shutdown(1);
                        return;
                    }

                    var editorApplyResult = window.RunSceneEditorDimensionSmoke(720, liveMode: false);
                    Logger.Info($"Dimension smoke scene editor apply result: configuredRows={editorApplyResult.configuredRows}, engineRows={editorApplyResult.engineRows}, engineColumns={editorApplyResult.engineColumns}, surface={editorApplyResult.surfaceWidth}x{editorApplyResult.surfaceHeight}, layerCount={editorApplyResult.layerCount}, allLayerRowsMatch={editorApplyResult.allLayerRowsMatch}, allLayerColumnsMatch={editorApplyResult.allLayerColumnsMatch}.");
                    if (editorApplyResult.configuredRows != 720 ||
                        editorApplyResult.engineRows != 720 ||
                        editorApplyResult.surfaceHeight != 720 ||
                        !editorApplyResult.allLayerRowsMatch ||
                        !editorApplyResult.allLayerColumnsMatch)
                    {
                        failure ??= new InvalidOperationException("Scene Editor deferred dimension change did not propagate to all simulation layers and the presentation surface.");
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

    private static int RunFrameProfileSmokeTest()
        => RunFrameProfileSmokeTest(240, "smoke-mainloop");

    private static int RunFrameProfileSmokeTest(int rows, string sessionName)
        => RunFrameProfileSmokeTest(rows, sessionName, rgbMode: false);

    private static int RunFrameProfileSmokeTest(int rows, string sessionName, bool rgbMode)
        => RunFrameProfileSmokeTest(rows, sessionName, rgbMode, null);

    private static int RunFrameProfileSmokeTest(int rows, string sessionName, bool rgbMode, string? smokeVideoPath)
    {
        Logger.Info("Running frame profile smoke test.");
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
                    window.ConfigureProfilingSmokeScene(rows, rgbMode, smokeVideoPath);
                    window.StartProfilingSession(sessionName);

                    var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                    {
                        Interval = TimeSpan.FromSeconds(rgbMode ? 8 : 4)
                    };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        try
                        {
                            var (report, path) = window.StopProfilingSessionAndExport();
                            var frameMetric = report.Metrics.FirstOrDefault(metric => metric.Name == "frame_total_ms");
                            if (frameMetric == null || frameMetric.Count < 30)
                            {
                                failure ??= new InvalidOperationException("Frame profiler did not collect enough frame samples.");
                                app.Shutdown(1);
                                return;
                            }

                            Logger.Info($"Frame profile report written to {path}");
                            foreach (var metric in report.Metrics
                                         .Where(metric => metric.Name.EndsWith("_ms", StringComparison.Ordinal))
                                         .OrderByDescending(metric => metric.Average)
                                         .Take(8))
                            {
                                Logger.Info($"Profile metric {metric.Name}: avg={metric.Average:F3} ms, p95={metric.P95:F3} ms, max={metric.Maximum:F3} ms, count={metric.Count}.");
                            }

                            window.Close();
                            app.Shutdown(0);
                        }
                        catch (Exception ex)
                        {
                            failure ??= ex;
                            window.Close();
                            app.Shutdown(1);
                        }
                    };
                    timer.Start();
                }), DispatcherPriority.ApplicationIdle);
            };

            window.Show();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Frame profile smoke test failed.", failure);
        }

        Logger.Info("Frame profile smoke test passed.");
        return exitCode;
    }

    private static int RunCurrentSceneProfileSmokeTest(bool visibleWindow)
    {
        Logger.Info($"Running current-scene profile smoke test (visibleWindow={visibleWindow}).");
        Exception? failure = null;
        App.LoadUserConfigInSmokeTest = true;

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
            var waitForWindow = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };

            waitForWindow.Tick += (_, _) =>
            {
                if (app.MainWindow is not MainWindow window)
                {
                    return;
                }

                waitForWindow.Stop();

                if (!visibleWindow)
                {
                    window.ShowInTaskbar = false;
                    window.ShowActivated = false;
                    window.Left = -10000;
                    window.Top = -10000;
                    window.Opacity = 0.0;
                }

                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    window.StartProfilingSession(visibleWindow ? "smoke-current-scene-visible" : "smoke-current-scene");

                    var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                    {
                        Interval = TimeSpan.FromSeconds(10)
                    };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        try
                        {
                            var (report, path) = window.StopProfilingSessionAndExport();
                            var frameMetric = report.Metrics.FirstOrDefault(metric => metric.Name == "frame_total_ms");
                            if (frameMetric == null || frameMetric.Count < 20)
                            {
                                failure ??= new InvalidOperationException("Current-scene profiler did not collect enough frame samples.");
                                app.Shutdown(1);
                                return;
                            }

                            Logger.Info($"Current-scene profile report written to {path}");
                            foreach (var metric in report.Metrics
                                         .Where(metric => metric.Name.EndsWith("_ms", StringComparison.Ordinal))
                                         .OrderByDescending(metric => metric.Average)
                                         .Take(16))
                            {
                                Logger.Info($"Profile metric {metric.Name}: avg={metric.Average:F3} ms, p95={metric.P95:F3} ms, max={metric.Maximum:F3} ms, count={metric.Count}.");
                            }

                            window.Close();
                            app.Shutdown(0);
                        }
                        catch (Exception ex)
                        {
                            failure ??= ex;
                            window.Close();
                            app.Shutdown(1);
                        }
                    };
                    timer.Start();
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Current-scene profile smoke test failed.", failure);
        }

        Logger.Info("Current-scene profile smoke test passed.");
        return exitCode;
    }

    private static int RunCurrentSceneInteractionProfileSmokeTest()
    {
        Logger.Info("Running current-scene interaction profile smoke test.");
        Exception? failure = null;
        App.LoadUserConfigInSmokeTest = true;

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
            var waitForWindow = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };

            waitForWindow.Tick += (_, _) =>
            {
                if (app.MainWindow is not MainWindow window)
                {
                    return;
                }

                waitForWindow.Stop();
                window.Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2));

                        window.StartProfilingSession("smoke-current-scene-pre-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (preReport, prePath) = window.StopProfilingSessionAndExport();

                        await window.OpenAndCloseRootContextMenuForSmokeAsync(TimeSpan.FromMilliseconds(750));
                        await Task.Delay(TimeSpan.FromSeconds(1));

                        window.StartProfilingSession("smoke-current-scene-post-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (postReport, postPath) = window.StopProfilingSessionAndExport();

                        var preGap = RequireMetric(preReport, "frame_tick_gap_ms");
                        var postGap = RequireMetric(postReport, "frame_tick_gap_ms");

                        Logger.Info($"Current-scene interaction pre profile written to {prePath}");
                        Logger.Info($"Current-scene interaction post profile written to {postPath}");
                        Logger.Info($"Pre interaction frame gap: avg={preGap.Average:F3} ms, p95={preGap.P95:F3} ms, max={preGap.Maximum:F3} ms.");
                        Logger.Info($"Post interaction frame gap: avg={postGap.Average:F3} ms, p95={postGap.P95:F3} ms, max={postGap.Maximum:F3} ms.");

                        if (preGap.Count < 20 || postGap.Count < 20)
                        {
                            throw new InvalidOperationException("Interaction profile smoke did not collect enough frame samples.");
                        }

                        if (postGap.Average > 25.0 || postGap.P95 > 35.0 || postGap.Average > preGap.Average + 5.0)
                        {
                            throw new InvalidOperationException(
                                $"Frame pacing did not recover after context menu interaction. Pre avg={preGap.Average:F3} ms, post avg={postGap.Average:F3} ms, post p95={postGap.P95:F3} ms.");
                        }

                        window.Close();
                        app.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        window.Close();
                        app.Shutdown(1);
                    }
                }, DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Current-scene interaction profile smoke test failed.", failure);
        }

        Logger.Info("Current-scene interaction profile smoke test passed.");
        return exitCode;
    }

    private static FrameProfileMetricReport RequireMetric(FrameProfileReport report, string name)
    {
        return report.Metrics.FirstOrDefault(metric => string.Equals(metric.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Expected profile metric '{name}' was not collected.");
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

            if (!window.RunGpuPassthroughCompositionSmoke())
            {
                failure ??= new InvalidOperationException("GPU passthrough composition did not stay on the GPU path through MainWindow.");
                app.Shutdown(1);
                return;
            }

            Logger.Info("GPU passthrough composition smoke test passed.");

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


