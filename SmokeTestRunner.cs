using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace lifeviz;

internal static class SmokeTestRunner
{
    private static readonly int[] CurrentScenePresetRows = { 144, 240, 480, 720, 1080, 1440, 2160 };
    private static readonly int[] RealtimePacingRows = { 144, 240, 480 };
    private static readonly TimeSpan CurrentSceneProfileWarmupDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CurrentScenePresetProfileDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RealtimePacingProfileDuration = TimeSpan.FromSeconds(8);
    private const double RealtimePacingTargetFps = 60.0;
    private static readonly string[] CurrentSceneBisectVariants =
    {
        "baseline",
        "no-audio",
        "no-video",
        "no-sim-groups",
        "first-static-only"
    };

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
            App.CaptureGpuFallbackBuffersInSmokeTest =
                !(target.StartsWith("profile-", StringComparison.OrdinalIgnoreCase) ||
                  target.StartsWith("pacing-", StringComparison.OrdinalIgnoreCase));
            string? smokeVideoPath = args.Length >= 3 ? args[2] : Environment.GetEnvironmentVariable("LIFEVIZ_SMOKE_VIDEO");
            if (TryRunCurrentScenePresetProfileTarget(target, out exitCode))
            {
                return true;
            }

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
                "profile-current-scene-fullscreen" => RunCurrentSceneProfileSmokeTest(visibleWindow: true, forcedRows: null, fullscreen: true),
                "profile-current-scene-bisect" => RunCurrentSceneBisectSmokeTest(),
                "profile-current-scene-presets" => RunCurrentScenePresetProfileSmokeSuite(visibleWindow: false),
                "profile-current-scene-visible-presets" => RunCurrentScenePresetProfileSmokeSuite(visibleWindow: true),
                "profile-current-scene-fullscreen-presets" => RunCurrentScenePresetProfileSmokeSuite(visibleWindow: true, fullscreen: true),
                "profile-current-scene-interaction" => RunCurrentSceneInteractionProfileSmokeTest(),
                "pacing-current-scene-visible-presets" => RunCurrentScenePacingSmokeSuite(visibleWindow: true),
                "pacing-current-scene-fullscreen-presets" => RunCurrentScenePacingSmokeSuite(visibleWindow: true, fullscreen: true),
                "pacing-current-scene-interaction" => RunCurrentSceneInteractionPacingSmokeTest(),
                "pacing-current-scene-overlay-fullscreen-144" => RunCurrentSceneOverlayPacingSmokeTest(fullscreen: true, rows: 144),
                "pacing-current-scene-suite" => RunCurrentScenePacingSuite(),
                "frame-pump-thread-safety" => RunFramePumpThreadSafetySmokeTest(),
                "gpu-benchmark" => RunGpuBenchmark(),
                "gpu-handoff" => RunGpuCompositeToSimulationSmokeTest(),
                "gpu-rgb-threshold" => RunGpuCompositeRgbThresholdSmokeTest(),
                "gpu-passthrough-signed-model" => RunGpuPassthroughSignedModelSmokeTest(),
                "passthrough-underlay-only" => RunPassthroughUnderlayOnlySmokeTest(),
                "gpu-frequency-hue" => RunGpuFrequencyHueSmokeTest(),
                "simulation-reactive-mappings" => RunSimulationReactiveMappingsSmokeTest(),
                "simulation-reactive-persistence" => RunSimulationReactiveMappingsPersistenceSmokeTest(),
                "simulation-reactive-legacy-migration" => RunSimulationReactiveLegacyMigrationSmokeTest(),
                "simulation-reactive-removal" => RunSimulationReactiveRemovalSmokeTest(),
                "simulation-reactive-editor-isolation" => RunSimulationReactiveEditorIsolationSmokeTest(),
                "sim-group-legacy-migration" => RunSimGroupLegacyMigrationSmokeTest(),
                "no-sim-group-renders-composite" => RunNoSimGroupRendersCompositeSmokeTest(),
                "sim-group-removal-clears-runtime" => RunSimGroupRemovalClearsRuntimeSmokeTest(),
                "disabled-sim-group-renders-composite" => RunDisabledSimGroupRendersCompositeSmokeTest(),
                "sim-group-stack-order" => RunSimGroupStackOrderSmokeTest(),
                "sim-group-inline-hue" => RunSimGroupInlineHueSmokeTest(),
                "sim-group-inline-presentation" => RunSimGroupInlinePresentationSmokeTest(),
                "sim-group-enabled-toggle" => RunSimGroupEnabledToggleSmokeTest(),
                "sim-group-remove-source" => RunSimGroupRemoveSourceSmokeTest(),
                "sim-group-live-edit-selection" => RunSimGroupLiveEditSelectionSmokeTest(),
                "gpu-injection-mode" => RunGpuInjectionModeSmokeTest(),
                "gpu-file-injection-mode" => RunGpuFileInjectionModeSmokeTest(smokeVideoPath),
                "gpu-sim" => RunGpuSimulationSmokeTest(),
                "gpu-source" => RunGpuSourceCompositeSmokeTest(),
                "source-reset" => RunSourceResetSmokeTest(),
                "gpu-render" => RunGpuPresentationSmokeTest(),
                "profile-mainloop" => RunFrameProfileSmokeTest(),
                "profile-mainloop-sim-group" => RunFrameProfileSmokeTest(240, "smoke-mainloop-sim-group", rgbMode: false, smokeVideoPath: null, includeSimGroup: true),
                "dimensions" => RunDimensionChangeSmokeTest(),
                "shutdown" => RunShutdownSmokeTest(),
                "startup" => RunStartupSmokeTest(),
                "startup-recovery" => RunStartupRecoverySmokeTest(),
                "all" => RunAllSmokeTests(),
                _ => throw new ArgumentException($"Unknown smoke test target '{target}'. Expected profile-240, profile-480, profile-rgb-240, profile-rgb-480, profile-file-240, profile-file-480, profile-file-rgb-240, profile-file-rgb-480, profile-current-scene, profile-current-scene-visible, profile-current-scene-fullscreen, profile-current-scene-bisect, profile-current-scene-presets, profile-current-scene-visible-presets, profile-current-scene-fullscreen-presets, profile-current-scene-<144|240|480|720|1080|1440|2160>, profile-current-scene-visible-<144|240|480|720|1080|1440|2160>, profile-current-scene-fullscreen-<144|240|480|720|1080|1440|2160>, profile-current-scene-interaction, pacing-current-scene-visible-presets, pacing-current-scene-fullscreen-presets, pacing-current-scene-interaction, pacing-current-scene-overlay-fullscreen-144, pacing-current-scene-suite, frame-pump-thread-safety, gpu-benchmark, gpu-handoff, gpu-rgb-threshold, gpu-passthrough-signed-model, passthrough-underlay-only, gpu-frequency-hue, simulation-reactive-mappings, simulation-reactive-persistence, simulation-reactive-legacy-migration, simulation-reactive-removal, simulation-reactive-editor-isolation, sim-group-legacy-migration, no-sim-group-renders-composite, sim-group-removal-clears-runtime, disabled-sim-group-renders-composite, sim-group-stack-order, sim-group-inline-hue, sim-group-inline-presentation, sim-group-enabled-toggle, sim-group-remove-source, sim-group-live-edit-selection, gpu-injection-mode, gpu-file-injection-mode, gpu-sim, gpu-source, source-reset, gpu-render, profile-mainloop, profile-mainloop-sim-group, dimensions, shutdown, startup, startup-recovery, or all.")
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
            App.CaptureGpuFallbackBuffersInSmokeTest = true;
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

    private static bool TryRunCurrentScenePresetProfileTarget(string target, out int exitCode)
    {
        exitCode = 0;
        const string hiddenPrefix = "profile-current-scene-";
        const string visiblePrefix = "profile-current-scene-visible-";
        const string fullscreenPrefix = "profile-current-scene-fullscreen-";

        bool visibleWindow;
        bool fullscreen;
        string? suffix;
        if (target.StartsWith(fullscreenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            visibleWindow = true;
            fullscreen = true;
            suffix = target.Substring(fullscreenPrefix.Length);
        }
        else if (target.StartsWith(visiblePrefix, StringComparison.OrdinalIgnoreCase))
        {
            visibleWindow = true;
            fullscreen = false;
            suffix = target.Substring(visiblePrefix.Length);
        }
        else if (target.StartsWith(hiddenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            visibleWindow = false;
            fullscreen = false;
            suffix = target.Substring(hiddenPrefix.Length);
        }
        else
        {
            return false;
        }

        if (!int.TryParse(suffix, out int rows) || !CurrentScenePresetRows.Contains(rows))
        {
            return false;
        }

        exitCode = RunCurrentSceneProfileSmokeTest(visibleWindow, rows, fullscreen);
        return true;
    }

    private static int RunFramePumpThreadSafetySmokeTest()
    {
        Logger.Info("Running frame pump thread-safety smoke test.");
        var app = new App();
        bool ok = false;
        Exception? capturedException = null;

        app.Startup += (_, _) =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                ok = window.RunFramePumpThreadSafetySmoke();
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }

                app.Shutdown();
            }
        };

        app.Run();

        if (capturedException != null)
        {
            throw capturedException;
        }

        if (!ok)
        {
            throw new InvalidOperationException("Frame pump thread-safety smoke test failed.");
        }

        Logger.Info("Frame pump thread-safety smoke test passed.");
        return 0;
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
            if (!IsKnownSmokeTeardownException(args.Exception))
            {
                failure ??= args.Exception;
            }
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

    private static int RunGpuPassthroughSignedModelSmokeTest()
    {
        Logger.Info("Running GPU passthrough signed-model smoke test.");
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
            bool ok = window.RunGpuPassthroughSignedModelSmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("GPU passthrough signed-model smoke did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("GPU passthrough signed-model smoke test failed.", failure);
        }

        Logger.Info("GPU passthrough signed-model smoke test passed.");
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

    private static int RunPassthroughUnderlayOnlySmokeTest()
    {
        Logger.Info("Running passthrough underlay-only smoke test.");
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
            bool ok = window.RunPassthroughUnderlayOnlySmoke();
            if (!ok)
            {
                failure ??= new InvalidOperationException("Passthrough underlay-only smoke did not complete successfully.");
                app.Shutdown(1);
                return;
            }

            app.Shutdown(0);
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Passthrough underlay-only smoke test failed.", failure);
        }

        Logger.Info("Passthrough underlay-only smoke test passed.");
        return exitCode;
    }

    private static int RunSimulationReactiveMappingsSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                bool ok = window.RunSimulationReactiveMappingsSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Simulation reactive mappings smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimulationReactiveMappingsPersistenceSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                bool ok = window.RunSimulationReactiveMappingsPersistenceSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Simulation reactive persistence smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimulationReactiveLegacyMigrationSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                bool ok = window.RunSimulationReactiveLegacyMigrationSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Simulation reactive legacy migration smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimulationReactiveRemovalSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                bool ok = window.RunSimulationReactiveRemovalSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Simulation reactive removal smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimulationReactiveEditorIsolationSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                var editor = new LayerEditorWindow(window);
                bool ok = editor.RunSimulationLayerReactiveIsolationSmoke();
                editor.Close();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Simulation reactive editor isolation smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupLegacyMigrationSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunLegacySimulationGroupSourceMigrationSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group legacy migration smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunNoSimGroupRendersCompositeSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunNoSimGroupRendersCompositeSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("No-sim-group composite smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupRemovalClearsRuntimeSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunSimGroupRemovalClearsRuntimeSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group removal clear-runtime smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunDisabledSimGroupRendersCompositeSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunDisabledSimGroupRendersCompositeSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Disabled-sim-group composite smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupStackOrderSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunSimGroupStackOrderSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group stack-order smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupInlineHueSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunSimGroupInlineHueSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group inline-hue smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupInlinePresentationSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                bool ok = window.RunSimGroupInlinePresentationFreshnessSmoke();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group inline-presentation smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupEnabledToggleSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                var editor = new LayerEditorWindow(window);
                bool ok = editor.RunSimGroupEnabledToggleSmoke();
                editor.Close();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group enabled-toggle smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupRemoveSourceSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                var editor = new LayerEditorWindow(window);
                bool ok = editor.RunSimGroupRemoveSourceSmoke();
                editor.Close();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group remove-source smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSimGroupLiveEditSelectionSmokeTest()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Hide();
                var editor = new LayerEditorWindow(window);
                bool ok = editor.RunSimGroupLiveEditSelectionSmoke();
                editor.Close();
                window.Close();
                app.Shutdown();
                exitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error("Sim-group live-edit selection smoke failed.", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
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
        => RunFrameProfileSmokeTest(rows, sessionName, rgbMode, smokeVideoPath, includeSimGroup: false);

    private static int RunFrameProfileSmokeTest(int rows, string sessionName, bool rgbMode, string? smokeVideoPath, bool includeSimGroup)
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
                    window.ConfigureProfilingSmokeScene(rows, rgbMode, smokeVideoPath, includeSimGroup);
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

                            if (!string.IsNullOrWhiteSpace(smokeVideoPath))
                            {
                                var freshMetric = report.Metrics.FirstOrDefault(metric => metric.Name == "capture_file_fresh_frame_ratio");
                                if (freshMetric == null || freshMetric.Count < 10)
                                {
                                    failure ??= new InvalidOperationException("File-video profiler did not record enough fresh-frame samples.");
                                    app.Shutdown(1);
                                    return;
                                }
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
        => RunCurrentSceneProfileSmokeTest(visibleWindow, forcedRows: null, fullscreen: false);

    private static int RunCurrentSceneBisectSmokeTest()
    {
        Logger.Info("Running current-scene bisect smoke test.");
        foreach (string variant in CurrentSceneBisectVariants)
        {
            RunCurrentSceneProfileSmokeVariant(variant, visibleWindow: true, forcedRows: 240, fullscreen: false);
        }

        Logger.Info("Current-scene bisect smoke test passed.");
        return 0;
    }

    private static int RunCurrentSceneProfileSmokeTest(bool visibleWindow, int? forcedRows, bool fullscreen)
        => RunCurrentSceneProfileSmokeVariant(null, visibleWindow, forcedRows, fullscreen);

    private static int RunCurrentSceneProfileSmokeVariant(string? variant, bool visibleWindow, int? forcedRows, bool fullscreen)
    {
        Logger.Info(forcedRows.HasValue
            ? $"Running current-scene profile smoke test (variant={variant ?? "baseline"}, visibleWindow={visibleWindow}, fullscreen={fullscreen}, rows={forcedRows.Value})."
            : $"Running current-scene profile smoke test (variant={variant ?? "baseline"}, visibleWindow={visibleWindow}, fullscreen={fullscreen}).");
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

                window.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(variant))
                        {
                            window.ApplyCurrentSceneBisectVariantForSmoke(variant);
                        }

                        if (forcedRows.HasValue)
                        {
                            int appliedRows = window.SetSimulationRowsForSmoke(forcedRows.Value);
                            if (appliedRows != forcedRows.Value)
                            {
                                throw new InvalidOperationException($"Requested current-scene smoke rows {forcedRows.Value} but engine applied {appliedRows}.");
                            }
                        }

                        if (fullscreen)
                        {
                            window.EnterFullscreenForSmoke();
                            await Task.Delay(250);
                            var (layoutOk, detail) = window.ValidateRenderLayoutForSmoke(fullscreenExpected: true);
                            if (!layoutOk)
                            {
                                throw new InvalidOperationException($"Fullscreen render layout validation failed before profiling. {detail}");
                            }
                        }

                        await Task.Delay(CurrentSceneProfileWarmupDuration);

                        string variantSuffix = string.IsNullOrWhiteSpace(variant) ? string.Empty : $"-{variant}";
                        string sessionName = forcedRows.HasValue
                            ? (fullscreen
                                ? $"smoke-current-scene-fullscreen-{forcedRows.Value}p{variantSuffix}"
                                : (visibleWindow ? $"smoke-current-scene-visible-{forcedRows.Value}p{variantSuffix}" : $"smoke-current-scene-{forcedRows.Value}p{variantSuffix}"))
                            : (fullscreen
                                ? $"smoke-current-scene-fullscreen{variantSuffix}"
                                : (visibleWindow ? $"smoke-current-scene-visible{variantSuffix}" : $"smoke-current-scene{variantSuffix}"));
                        window.StartProfilingSession(sessionName);

                        await Task.Delay(CurrentScenePresetProfileDuration);

                        var (report, path) = window.StopProfilingSessionAndExport();
                        ValidateSettledCurrentSceneProfile(
                            report,
                            forcedRows.HasValue
                                ? $"{variant ?? "baseline"} {forcedRows.Value}p {(fullscreen ? "fullscreen" : (visibleWindow ? "visible" : "hidden"))}"
                                : $"{variant ?? "baseline"} current-scene {(fullscreen ? "fullscreen" : (visibleWindow ? "visible" : "hidden"))}");

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
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int exitCode = app.Run();
        if (failure != null && !IsKnownSmokeTeardownException(failure))
        {
            throw new InvalidOperationException("Current-scene profile smoke test failed.", failure);
        }

        Logger.Info("Current-scene profile smoke test passed.");
        return exitCode;
    }

    private static int RunCurrentScenePresetProfileSmokeSuite(bool visibleWindow, bool fullscreen = false)
    {
        Logger.Info($"Running current-scene preset profile smoke suite (visibleWindow={visibleWindow}, fullscreen={fullscreen}).");
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

                window.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                {
                    try
                    {
                        foreach (int rows in CurrentScenePresetRows)
                        {
                            int appliedRows = window.SetSimulationRowsForSmoke(rows);
                            if (appliedRows != rows)
                            {
                                throw new InvalidOperationException($"Requested preset smoke rows {rows} but engine applied {appliedRows}.");
                            }

                            if (fullscreen)
                            {
                                window.EnterFullscreenForSmoke();
                                await Task.Delay(150);
                                var (layoutOk, detail) = window.ValidateRenderLayoutForSmoke(fullscreenExpected: true);
                                if (!layoutOk)
                                {
                                    throw new InvalidOperationException($"Fullscreen render layout validation failed for {rows}p. {detail}");
                                }
                            }

                            string sessionName = fullscreen
                                ? $"smoke-current-scene-fullscreen-{rows}p"
                                : (visibleWindow ? $"smoke-current-scene-visible-{rows}p" : $"smoke-current-scene-{rows}p");
                            await Task.Delay(CurrentSceneProfileWarmupDuration);
                            window.StartProfilingSession(sessionName);
                            await Task.Delay(CurrentScenePresetProfileDuration);

                            var (report, path) = window.StopProfilingSessionAndExport();
                            ValidateSettledCurrentSceneProfile(report, $"{rows}p {(fullscreen ? "fullscreen" : (visibleWindow ? "visible" : "hidden"))}");

                            Logger.Info($"Current-scene preset profile report written to {path}");
                            foreach (var metric in report.Metrics
                                         .Where(metric => metric.Name.EndsWith("_ms", StringComparison.Ordinal))
                                         .OrderByDescending(metric => metric.Average)
                                         .Take(12))
                            {
                                Logger.Info($"[{rows}p] Profile metric {metric.Name}: avg={metric.Average:F3} ms, p95={metric.P95:F3} ms, max={metric.Maximum:F3} ms, count={metric.Count}.");
                            }
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
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Current-scene preset profile smoke suite failed.", failure);
        }

        Logger.Info("Current-scene preset profile smoke suite passed.");
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

                        window.OpenLayerEditor();
                        await Task.Delay(TimeSpan.FromMilliseconds(750));
                        window.Activate();
                        await Task.Delay(TimeSpan.FromMilliseconds(500));

                        window.StartProfilingSession("smoke-current-scene-editor-open");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (editorReport, editorPath) = window.StopProfilingSessionAndExport();

                        await window.OpenAndCloseRootContextMenuForSmokeAsync(TimeSpan.FromMilliseconds(750));
                        await Task.Delay(TimeSpan.FromSeconds(1));

                        window.StartProfilingSession("smoke-current-scene-post-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (postReport, postPath) = window.StopProfilingSessionAndExport();

                        var preGap = RequireMetric(preReport, "frame_tick_gap_ms");
                        var editorGap = RequireMetric(editorReport, "frame_tick_gap_ms");
                        var postGap = RequireMetric(postReport, "frame_tick_gap_ms");
                        var editorThrottle = RequireMetric(editorReport, "ui_interaction_throttled");
                        var postThrottle = RequireMetric(postReport, "ui_interaction_throttled");

                        Logger.Info($"Current-scene interaction pre profile written to {prePath}");
                        Logger.Info($"Current-scene interaction editor-open profile written to {editorPath}");
                        Logger.Info($"Current-scene interaction post profile written to {postPath}");
                        Logger.Info($"Pre interaction frame gap: avg={preGap.Average:F3} ms, p95={preGap.P95:F3} ms, max={preGap.Maximum:F3} ms.");
                        Logger.Info($"Editor-open frame gap: avg={editorGap.Average:F3} ms, p95={editorGap.P95:F3} ms, max={editorGap.Maximum:F3} ms.");
                        Logger.Info($"Post interaction frame gap: avg={postGap.Average:F3} ms, p95={postGap.P95:F3} ms, max={postGap.Maximum:F3} ms.");

                        if (preGap.Count < 20 || editorGap.Count < 20 || postGap.Count < 20)
                        {
                            throw new InvalidOperationException("Interaction profile smoke did not collect enough frame samples.");
                        }

                        if (editorThrottle.Average > 0.10)
                        {
                            throw new InvalidOperationException(
                                $"Main window remained throttled while the Scene Editor was open. ui_interaction_throttled avg={editorThrottle.Average:F3}.");
                        }

                        if (postThrottle.Average > 0.10)
                        {
                            throw new InvalidOperationException(
                                $"Main window remained throttled after interaction recovery. ui_interaction_throttled avg={postThrottle.Average:F3}.");
                        }

                        if (editorGap.Average > 25.0 || editorGap.P95 > 35.0 || editorGap.Average > preGap.Average + 5.0)
                        {
                            throw new InvalidOperationException(
                                $"Frame pacing degraded while the Scene Editor was open. Pre avg={preGap.Average:F3} ms, editor avg={editorGap.Average:F3} ms, editor p95={editorGap.P95:F3} ms.");
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

    private static int RunCurrentScenePacingSuite()
    {
        int visibleResult = RunCurrentScenePacingSmokeSuite(visibleWindow: true);
        if (visibleResult != 0)
        {
            return visibleResult;
        }

        return RunCurrentSceneInteractionPacingSmokeTest();
    }

    private static int RunCurrentScenePacingSmokeSuite(bool visibleWindow, bool fullscreen = false)
    {
        Logger.Info($"Running current-scene pacing smoke suite (visibleWindow={visibleWindow}, fullscreen={fullscreen}).");
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

                window.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                {
                    try
                    {
                        foreach (int rows in RealtimePacingRows)
                        {
                            double appliedFps = window.ConfigurePacingSmokeScenario(rows, RealtimePacingTargetFps);
                            if (Math.Abs(appliedFps - RealtimePacingTargetFps) > 0.1)
                            {
                                throw new InvalidOperationException($"Requested pacing target {RealtimePacingTargetFps:F1} fps but engine applied {appliedFps:F1} fps.");
                            }

                            if (fullscreen)
                            {
                                window.EnterFullscreenForSmoke();
                                await Task.Delay(750);
                                var (layoutOk, detail) = window.ValidateRenderLayoutForSmoke(fullscreenExpected: true);
                                if (!layoutOk)
                                {
                                    throw new InvalidOperationException($"Fullscreen render layout validation failed for pacing suite at {rows}p. {detail}");
                                }
                            }

                            string sessionName = fullscreen
                                ? $"smoke-current-scene-pacing-fullscreen-{rows}p"
                                : $"smoke-current-scene-pacing-visible-{rows}p";
                            window.StartProfilingSession(sessionName);
                            await Task.Delay(RealtimePacingProfileDuration);

                            var (report, path) = window.StopProfilingSessionAndExport();
                            ValidateRealtimePacing(report, $"{rows}p {(fullscreen ? "fullscreen" : "visible")}", RealtimePacingTargetFps);

                            Logger.Info($"Current-scene pacing profile report written to {path}");
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
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Current-scene pacing smoke suite failed.", failure);
        }

        Logger.Info("Current-scene pacing smoke suite passed.");
        return exitCode;
    }

    private static int RunCurrentSceneInteractionPacingSmokeTest()
    {
        Logger.Info("Running current-scene interaction pacing smoke test.");
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
                        window.ConfigurePacingSmokeScenario(480, RealtimePacingTargetFps);
                        await Task.Delay(TimeSpan.FromSeconds(2));

                        window.StartProfilingSession("smoke-current-scene-pacing-pre-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (preReport, prePath) = window.StopProfilingSessionAndExport();
                        ValidateRealtimePacing(preReport, "480p visible pre-interaction", RealtimePacingTargetFps);

                        window.OpenLayerEditor();
                        await Task.Delay(TimeSpan.FromMilliseconds(750));
                        window.Activate();
                        await Task.Delay(TimeSpan.FromMilliseconds(500));

                        window.StartProfilingSession("smoke-current-scene-pacing-editor-open");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (editorReport, editorPath) = window.StopProfilingSessionAndExport();
                        ValidateRealtimePacing(editorReport, "480p visible editor-open", RealtimePacingTargetFps);

                        await window.OpenAndCloseRootContextMenuForSmokeAsync(TimeSpan.FromMilliseconds(750));
                        await Task.Delay(TimeSpan.FromSeconds(1));

                        window.StartProfilingSession("smoke-current-scene-pacing-post-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (postReport, postPath) = window.StopProfilingSessionAndExport();
                        ValidateRealtimePacing(postReport, "480p visible post-interaction", RealtimePacingTargetFps);

                        var editorThrottle = RequireMetric(editorReport, "ui_interaction_throttled");
                        var postThrottle = RequireMetric(postReport, "ui_interaction_throttled");

                        Logger.Info($"Current-scene interaction pacing pre profile written to {prePath}");
                        Logger.Info($"Current-scene interaction pacing editor profile written to {editorPath}");
                        Logger.Info($"Current-scene interaction pacing post profile written to {postPath}");

                        if (editorThrottle.Average > 0.10)
                        {
                            throw new InvalidOperationException(
                                $"Main window remained throttled while the Scene Editor was open during pacing smoke. ui_interaction_throttled avg={editorThrottle.Average:F3}.");
                        }

                        if (postThrottle.Average > 0.10)
                        {
                            throw new InvalidOperationException(
                                $"Main window remained throttled after context menu recovery during pacing smoke. ui_interaction_throttled avg={postThrottle.Average:F3}.");
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
            throw new InvalidOperationException("Current-scene interaction pacing smoke test failed.", failure);
        }

        Logger.Info("Current-scene interaction pacing smoke test passed.");
        return exitCode;
    }

    private static int RunCurrentSceneOverlayPacingSmokeTest(bool fullscreen, int rows)
    {
        Logger.Info($"Running current-scene overlay pacing smoke test (fullscreen={fullscreen}, rows={rows}).");
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
                window.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                {
                    try
                    {
                        window.ConfigurePacingSmokeScenario(rows, RealtimePacingTargetFps);
                        if (fullscreen)
                        {
                            window.EnterFullscreenForSmoke();
                            await Task.Delay(750);
                            var (layoutOk, detail) = window.ValidateRenderLayoutForSmoke(fullscreenExpected: true);
                            if (!layoutOk)
                            {
                                throw new InvalidOperationException($"Fullscreen render layout validation failed for overlay pacing smoke at {rows}p. {detail}");
                            }
                        }

                        window.SetShowFpsForSmoke(true);
                        await Task.Delay(1000);

                        string sessionName = fullscreen
                            ? $"smoke-current-scene-overlay-fullscreen-{rows}p"
                            : $"smoke-current-scene-overlay-visible-{rows}p";
                        window.StartProfilingSession(sessionName);
                        await Task.Delay(RealtimePacingProfileDuration);

                        var (report, path) = window.StopProfilingSessionAndExport();
                        ValidateRealtimePacing(report, $"{rows}p {(fullscreen ? "fullscreen" : "visible")} overlay", RealtimePacingTargetFps);

                        var overlayMetric = RequireMetric(report, "fps_overlay_ms");
                        Logger.Info($"Current-scene overlay pacing profile report written to {path}");
                        Logger.Info($"Overlay timing: avg={overlayMetric.Average:F3} ms, p95={overlayMetric.P95:F3} ms, max={overlayMetric.Maximum:F3} ms.");

                        window.Close();
                        app.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        window.Close();
                        app.Shutdown(1);
                    }
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int exitCode = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Current-scene overlay pacing smoke test failed.", failure);
        }

        Logger.Info("Current-scene overlay pacing smoke test passed.");
        return exitCode;
    }

    private static FrameProfileMetricReport RequireMetric(FrameProfileReport report, string name)
    {
        return report.Metrics.FirstOrDefault(metric => string.Equals(metric.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Expected profile metric '{name}' was not collected.");
    }

    private static bool IsKnownSmokeTeardownException(Exception? exception)
    {
        if (exception == null)
        {
            return false;
        }

        string text = exception.ToString();
        return text.Contains("Vortice.Wpf.DrawingSurface.EndD3D()", StringComparison.Ordinal) &&
               text.Contains("NullReferenceException", StringComparison.Ordinal);
    }

    private static void ValidateSettledCurrentSceneProfile(FrameProfileReport report, string label)
    {
        var frameMetric = RequireMetric(report, "frame_total_ms");
        if (frameMetric.Count < 60)
        {
            throw new InvalidOperationException($"{label}: current-scene profile did not collect enough settled frame samples ({frameMetric.Count}).");
        }

        var frameGap = RequireMetric(report, "frame_tick_gap_ms");
        var over50 = RequireMetric(report, "frame_gap_over_50ms");
        if (frameGap.P95 > 75.0)
        {
            throw new InvalidOperationException($"{label}: settled p95 frame gap {frameGap.P95:F3} ms is too high.");
        }

        if (over50.Average > 0.05)
        {
            throw new InvalidOperationException($"{label}: settled ratio of frame gaps over 50 ms was {over50.Average:P1}, expected <= 5.0%.");
        }

        var presentFps = report.Metrics.FirstOrDefault(metric => string.Equals(metric.Name, "presentation_draw_fps", StringComparison.Ordinal));
        if (presentFps != null && presentFps.Count > 0 && presentFps.Average < 45.0)
        {
            throw new InvalidOperationException($"{label}: present cadence regressed; average present fps was only {presentFps.Average:F2}.");
        }

        foreach (var ageMetric in report.Metrics.Where(metric =>
                     (metric.Name.StartsWith("capture_file_frame_age_ms", StringComparison.Ordinal) ||
                      metric.Name.StartsWith("capture_sequence_frame_age_ms", StringComparison.Ordinal)) &&
                     metric.Count >= 30))
        {
            if (ageMetric.P95 > 250.0)
            {
                throw new InvalidOperationException($"{label}: source freshness regressed for {ageMetric.Name}; p95 frame age was {ageMetric.P95:F3} ms.");
            }
        }

        foreach (var ratioMetric in report.Metrics.Where(metric =>
                     (metric.Name.StartsWith("capture_file_fresh_frame_ratio", StringComparison.Ordinal) ||
                      metric.Name.StartsWith("capture_sequence_fresh_frame_ratio", StringComparison.Ordinal)) &&
                     metric.Count >= 30))
        {
            if (ratioMetric.Average < 0.10)
            {
                throw new InvalidOperationException($"{label}: source freshness regressed for {ratioMetric.Name}; fresh-frame ratio averaged only {ratioMetric.Average:P1}.");
            }
        }
    }

    private static void ValidateRealtimePacing(FrameProfileReport report, string label, double targetFps)
    {
        double frameBudgetMs = 1000.0 / Math.Max(1.0, targetFps);
        var frameGap = RequireMetric(report, "frame_tick_gap_ms");
        var over25 = RequireMetric(report, "frame_gap_over_25ms");
        var over33 = RequireMetric(report, "frame_gap_over_33ms");
        var over50 = RequireMetric(report, "frame_gap_over_50ms");

        if (frameGap.Count < 60)
        {
            throw new InvalidOperationException($"{label}: pacing smoke did not collect enough frame-gap samples ({frameGap.Count}).");
        }

        double maxAverageGap = frameBudgetMs * 1.12;
        double maxP95Gap = frameBudgetMs * 1.35;
        double maxP99Gap = frameBudgetMs * 1.80;

        if (frameGap.Average > maxAverageGap)
        {
            throw new InvalidOperationException($"{label}: average frame gap {frameGap.Average:F3} ms exceeded pacing budget {maxAverageGap:F3} ms.");
        }

        if (frameGap.P95 > maxP95Gap)
        {
            throw new InvalidOperationException($"{label}: p95 frame gap {frameGap.P95:F3} ms exceeded pacing budget {maxP95Gap:F3} ms.");
        }

        if (frameGap.P99 > maxP99Gap)
        {
            throw new InvalidOperationException($"{label}: p99 frame gap {frameGap.P99:F3} ms exceeded pacing budget {maxP99Gap:F3} ms.");
        }

        if (over25.Average > 0.02)
        {
            throw new InvalidOperationException($"{label}: underrun ratio over 25 ms was {over25.Average:P1}, expected <= 2.0%.");
        }

        if (over33.Average > 0.01)
        {
            throw new InvalidOperationException($"{label}: underrun ratio over 33 ms was {over33.Average:P1}, expected <= 1.0%.");
        }

        if (over50.Average > 0.005)
        {
            throw new InvalidOperationException($"{label}: detected too many frame gaps over 50 ms (ratio {over50.Average:P1}), expected <= 0.5%.");
        }
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

    private static int RunStartupRecoverySmokeTest()
    {
        Logger.Info("Running startup recovery smoke test.");
        Exception? failure = null;
        App.LoadUserConfigInSmokeTest = true;
        WriteStartupRecoveryFlagForSmoke();

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
                    var state = window.GetStartupRecoveryStateForSmoke();
                    string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "config.json");
                    string persistedJson = File.ReadAllText(configPath);
                    using JsonDocument persistedDocument = JsonDocument.Parse(persistedJson);
                    JsonElement root = persistedDocument.RootElement;
                    int persistedRows = root.TryGetProperty("Height", out var heightProperty) ? heightProperty.GetInt32() : 0;
                    double persistedFps = root.TryGetProperty("Framerate", out var fpsProperty) ? fpsProperty.GetDouble() : 0;
                    bool persistedFullscreen = root.TryGetProperty("Fullscreen", out var fullscreenProperty) && fullscreenProperty.GetBoolean();
                    bool persistedShowFps = root.TryGetProperty("ShowFps", out var showFpsProperty) && showFpsProperty.GetBoolean();
                    bool persistedLevelToFramerate = root.TryGetProperty("AudioReactiveLevelToFpsEnabled", out var levelToFpsProperty) && levelToFpsProperty.GetBoolean();
                    if (state.rows > 480 ||
                        state.renderFps > 60.0 ||
                        state.simulationFps > 60.0 ||
                        state.fullscreen ||
                        state.showFps ||
                        state.levelToFramerate ||
                        persistedRows > 480 ||
                        persistedFps > 60.0 ||
                        persistedFullscreen ||
                        persistedShowFps ||
                        persistedLevelToFramerate ||
                        state.sourceCount <= 0)
                    {
                        failure ??= new InvalidOperationException(
                            $"Startup recovery did not apply safe launch overrides before scene restore. " +
                            $"rows={state.rows}, renderFps={state.renderFps:0.##}, simFps={state.simulationFps:0.##}, fullscreen={state.fullscreen}, showFps={state.showFps}, levelToFramerate={state.levelToFramerate}, " +
                            $"persistedRows={persistedRows}, persistedFps={persistedFps:0.##}, persistedFullscreen={persistedFullscreen}, persistedShowFps={persistedShowFps}, persistedLevelToFramerate={persistedLevelToFramerate}, sourceCount={state.sourceCount}.");
                    }

                    window.Close();
                    app.Shutdown(failure == null ? 0 : 1);
                };
                timer.Start();
            };

            window.Show();
        };

        int exitCode = app.Run();
        ClearStartupRecoveryFlagForSmoke();
        if (failure != null)
        {
            throw new InvalidOperationException("Startup recovery smoke test failed.", failure);
        }

        Logger.Info("Startup recovery smoke test passed.");
        return exitCode;
    }

    private static string GetStartupRecoveryFlagPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "startup-recovery.flag");
    }

    private static void WriteStartupRecoveryFlagForSmoke()
    {
        string path = GetStartupRecoveryFlagPath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
    }

    private static void ClearStartupRecoveryFlagForSmoke()
    {
        string path = GetStartupRecoveryFlagPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
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


