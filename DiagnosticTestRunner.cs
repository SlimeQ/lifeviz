using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace lifeviz;

internal static class DiagnosticTestRunner
{
    private static readonly int[] CurrentScenePresetRows = { 144, 240, 480, 720, 1080, 1440, 2160 };
    private static readonly TimeSpan CurrentSceneProfileWarmupDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CurrentSceneProfileDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CurrentSceneSoakDuration = TimeSpan.FromSeconds(30);

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !string.Equals(args[0], "--diagnostic-test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Logger.Initialize();
        App.SuppressErrorDialogs = true;
        App.IsDiagnosticTestMode = true;

        try
        {
            string target = args[1].Trim();
            exitCode = target.ToLowerInvariant() switch
            {
                "profile-current-scene-visible" => RunCurrentSceneProfileDiagnostic(visibleWindow: true, forcedRows: null, fullscreen: false),
                "profile-current-scene-fullscreen" => RunCurrentSceneProfileDiagnostic(visibleWindow: true, forcedRows: null, fullscreen: true),
                "profile-current-scene-interaction" => RunCurrentSceneInteractionDiagnostic(),
                "profile-current-scene-soak-720" => RunCurrentSceneSoakDiagnostic(rows: 720),
                "profile-current-scene-visible-presets" => RunCurrentScenePresetProfileDiagnostic(visibleWindow: true, fullscreen: false),
                "profile-current-scene-fullscreen-presets" => RunCurrentScenePresetProfileDiagnostic(visibleWindow: true, fullscreen: true),
                _ when TryRunCurrentScenePresetProfileTarget(target, out int presetExitCode) => presetExitCode,
                _ => throw new ArgumentException(
                    $"Unknown diagnostic target '{target}'. Expected profile-current-scene-visible, profile-current-scene-fullscreen, profile-current-scene-interaction, profile-current-scene-soak-720, " +
                    "profile-current-scene-visible-presets, profile-current-scene-fullscreen-presets, " +
                    "profile-current-scene-visible-<144|240|480|720|1080|1440|2160>, or profile-current-scene-fullscreen-<144|240|480|720|1080|1440|2160>.")
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Diagnostic test failed.", ex);
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }
        finally
        {
            App.IsDiagnosticTestMode = false;
            App.SuppressErrorDialogs = false;
            Logger.Shutdown();
        }

        return true;
    }

    private static bool TryRunCurrentScenePresetProfileTarget(string target, out int exitCode)
    {
        exitCode = 0;
        const string visiblePrefix = "profile-current-scene-visible-";
        const string fullscreenPrefix = "profile-current-scene-fullscreen-";

        bool fullscreen;
        string? suffix;
        if (target.StartsWith(fullscreenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fullscreen = true;
            suffix = target.Substring(fullscreenPrefix.Length);
        }
        else if (target.StartsWith(visiblePrefix, StringComparison.OrdinalIgnoreCase))
        {
            fullscreen = false;
            suffix = target.Substring(visiblePrefix.Length);
        }
        else
        {
            return false;
        }

        if (!int.TryParse(suffix, out int rows) || !CurrentScenePresetRows.Contains(rows))
        {
            return false;
        }

        exitCode = RunCurrentSceneProfileDiagnostic(visibleWindow: true, forcedRows: rows, fullscreen: fullscreen);
        return true;
    }

    private static int RunCurrentSceneProfileDiagnostic(bool visibleWindow, int? forcedRows, bool fullscreen)
    {
        Logger.Info(forcedRows.HasValue
            ? $"Running diagnostic current-scene profile (visibleWindow={visibleWindow}, fullscreen={fullscreen}, rows={forcedRows.Value})."
            : $"Running diagnostic current-scene profile (visibleWindow={visibleWindow}, fullscreen={fullscreen}).");

        Exception? failure = null;
        bool closePending = false;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            if (closePending && IsIgnorableDrawingSurfaceShutdownException(args.Exception))
            {
                args.Handled = true;
                return;
            }

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
                        if (forcedRows.HasValue)
                        {
                            int appliedRows = window.SetSimulationRowsForSmoke(forcedRows.Value);
                            if (appliedRows != forcedRows.Value)
                            {
                                throw new InvalidOperationException($"Requested diagnostic rows {forcedRows.Value} but engine applied {appliedRows}.");
                            }
                        }

                        if (fullscreen)
                        {
                            window.EnterFullscreenForSmoke();
                            await Task.Delay(750);
                            var (layoutOk, detail) = window.ValidateRenderLayoutForSmoke(fullscreenExpected: true);
                            if (!layoutOk)
                            {
                                throw new InvalidOperationException($"Fullscreen render layout validation failed before diagnostic profiling. {detail}");
                            }
                        }

                        await Task.Delay(CurrentSceneProfileWarmupDuration);

                        string sessionName = forcedRows.HasValue
                            ? (fullscreen
                                ? $"diagnostic-current-scene-fullscreen-{forcedRows.Value}p"
                                : $"diagnostic-current-scene-visible-{forcedRows.Value}p")
                            : (fullscreen ? "diagnostic-current-scene-fullscreen" : "diagnostic-current-scene-visible");
                        window.StartProfilingSession(sessionName);
                        await Task.Delay(CurrentSceneProfileDuration);

                        string outputDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles");
                        var (report, path) = window.StopProfilingSessionAndExport(outputDirectory);
                        ValidateSettledCurrentSceneProfile(
                            report,
                            forcedRows.HasValue
                                ? $"{forcedRows.Value}p {(fullscreen ? "fullscreen" : "visible")} diagnostic"
                                : $"current-scene {(fullscreen ? "fullscreen" : "visible")} diagnostic");

                        Logger.Info($"Diagnostic current-scene profile report written to {path}");
                        foreach (var metric in report.Metrics
                                     .Where(metric => metric.Name.EndsWith("_ms", StringComparison.Ordinal))
                                     .OrderByDescending(metric => metric.Average)
                                     .Take(16))
                        {
                            Logger.Info($"Diagnostic metric {metric.Name}: avg={metric.Average:F3} ms, p95={metric.P95:F3} ms, max={metric.Maximum:F3} ms, count={metric.Count}.");
                        }

                        closePending = true;
                        window.Close();
                        app.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        closePending = true;
                        window.Close();
                        app.Shutdown(1);
                    }
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int result = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Diagnostic current-scene profile failed.", failure);
        }

        Logger.Info("Diagnostic current-scene profile passed.");
        return result;
    }

    private static int RunCurrentScenePresetProfileDiagnostic(bool visibleWindow, bool fullscreen)
    {
        foreach (int rows in CurrentScenePresetRows)
        {
            int result = RunCurrentSceneProfileDiagnostic(visibleWindow, rows, fullscreen);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static int RunCurrentSceneInteractionDiagnostic()
    {
        Logger.Info("Running diagnostic current-scene interaction profile.");

        Exception? failure = null;
        bool closePending = false;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            if (closePending && IsIgnorableDrawingSurfaceShutdownException(args.Exception))
            {
                args.Handled = true;
                return;
            }

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
                        await Task.Delay(TimeSpan.FromSeconds(4));

                        string outputDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles");

                        window.StartProfilingSession("diagnostic-current-scene-pre-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (preReport, prePath) = window.StopProfilingSessionAndExport(outputDirectory);

                        window.OpenLayerEditor();
                        await Task.Delay(TimeSpan.FromMilliseconds(750));
                        window.Activate();
                        await Task.Delay(TimeSpan.FromMilliseconds(500));

                        window.StartProfilingSession("diagnostic-current-scene-editor-open");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (editorReport, editorPath) = window.StopProfilingSessionAndExport(outputDirectory);

                        await window.OpenAndCloseRootContextMenuForSmokeAsync(TimeSpan.FromMilliseconds(750));
                        await Task.Delay(TimeSpan.FromSeconds(1));

                        window.StartProfilingSession("diagnostic-current-scene-post-interaction");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var (postReport, postPath) = window.StopProfilingSessionAndExport(outputDirectory);

                        var preGap = RequireMetric(preReport, "frame_tick_gap_ms");
                        var editorGap = RequireMetric(editorReport, "frame_tick_gap_ms");
                        var postGap = RequireMetric(postReport, "frame_tick_gap_ms");

                        Logger.Info($"Diagnostic interaction pre profile written to {prePath}");
                        Logger.Info($"Diagnostic interaction editor profile written to {editorPath}");
                        Logger.Info($"Diagnostic interaction post profile written to {postPath}");
                        Logger.Info($"Diagnostic interaction gaps: pre avg={preGap.Average:F3} p95={preGap.P95:F3}; editor avg={editorGap.Average:F3} p95={editorGap.P95:F3}; post avg={postGap.Average:F3} p95={postGap.P95:F3}.");

                        if (editorGap.Average > preGap.Average + 5.0 || editorGap.P95 > 35.0)
                        {
                            throw new InvalidOperationException(
                                $"Editor-open pacing degraded in normal startup. pre avg={preGap.Average:F3} ms, editor avg={editorGap.Average:F3} ms, editor p95={editorGap.P95:F3} ms.");
                        }

                        if (postGap.Average > preGap.Average + 5.0 || postGap.P95 > 35.0)
                        {
                            throw new InvalidOperationException(
                                $"Post-menu pacing degraded in normal startup. pre avg={preGap.Average:F3} ms, post avg={postGap.Average:F3} ms, post p95={postGap.P95:F3} ms.");
                        }

                        closePending = true;
                        window.Close();
                        app.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        closePending = true;
                        window.Close();
                        app.Shutdown(1);
                    }
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int result = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Diagnostic current-scene interaction profile failed.", failure);
        }

        Logger.Info("Diagnostic current-scene interaction profile passed.");
        return result;
    }

    private static int RunCurrentSceneSoakDiagnostic(int rows)
    {
        Logger.Info($"Running diagnostic current-scene soak profile at {rows}p.");

        Exception? failure = null;
        bool closePending = false;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, args) =>
        {
            if (closePending && IsIgnorableDrawingSurfaceShutdownException(args.Exception))
            {
                args.Handled = true;
                return;
            }

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
                        int appliedRows = window.SetSimulationRowsForSmoke(rows);
                        if (appliedRows != rows)
                        {
                            throw new InvalidOperationException($"Requested soak rows {rows} but engine applied {appliedRows}.");
                        }

                        await Task.Delay(CurrentSceneProfileWarmupDuration);

                        string outputDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles");
                        string sessionName = $"diagnostic-current-scene-soak-{rows}p";
                        window.StartProfilingSession(sessionName);
                        await Task.Delay(CurrentSceneSoakDuration);

                        var (report, path) = window.StopProfilingSessionAndExport(outputDirectory);
                        ValidateSettledCurrentSceneProfile(report, $"{rows}p soak diagnostic");

                        Logger.Info($"Diagnostic soak profile written to {path}");
                        foreach (var metric in report.Metrics
                                     .Where(metric => metric.Name.EndsWith("_ms", StringComparison.Ordinal))
                                     .OrderByDescending(metric => metric.Average)
                                     .Take(16))
                        {
                            Logger.Info($"Diagnostic soak metric {metric.Name}: avg={metric.Average:F3} ms, p95={metric.P95:F3} ms, max={metric.Maximum:F3} ms, count={metric.Count}.");
                        }

                        closePending = true;
                        window.Close();
                        app.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        closePending = true;
                        window.Close();
                        app.Shutdown(1);
                    }
                }), DispatcherPriority.ApplicationIdle);
            };

            waitForWindow.Start();
        };

        int result = app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Diagnostic current-scene soak profile failed.", failure);
        }

        Logger.Info("Diagnostic current-scene soak profile passed.");
        return result;
    }

    private static bool IsIgnorableDrawingSurfaceShutdownException(Exception ex)
    {
        if (ex is not NullReferenceException)
        {
            return false;
        }

        string stack = ex.StackTrace ?? string.Empty;
        return stack.Contains("Vortice.Wpf.DrawingSurface.EndD3D", StringComparison.Ordinal) ||
               stack.Contains("Vortice.Wpf.DrawingSurface.Window_Closed", StringComparison.Ordinal);
    }

    private static void ValidateSettledCurrentSceneProfile(FrameProfileReport report, string label)
    {
        if (report == null)
        {
            throw new InvalidOperationException($"Settled diagnostic report missing for {label}.");
        }

        var frameGap = RequireMetric(report, "frame_tick_gap_ms");
        if (frameGap.Count < 60)
        {
            throw new InvalidOperationException($"Settled diagnostic for {label} collected too few frame-gap samples ({frameGap.Count}).");
        }

        if (frameGap.P95 > 75.0)
        {
            throw new InvalidOperationException($"Settled diagnostic for {label} exceeded frame-gap budget. p95={frameGap.P95:F3} ms.");
        }

        if (TryGetMetric(report, "frame_gap_over_50ms", out var over50) && over50.Count > 0 && over50.Average > 0.05)
        {
            throw new InvalidOperationException($"Settled diagnostic for {label} had too many >50ms frame gaps. average={over50.Average:F3}.");
        }

        if (TryGetMetric(report, "presentation_draw_fps", out var presentationFps) && presentationFps.Count > 0 && presentationFps.Average < 45.0)
        {
            throw new InvalidOperationException(
                $"Settled diagnostic for {label} failed presentation cadence. average present fps={presentationFps.Average:F3}.");
        }

        foreach (var metric in report.Metrics.Where(metric =>
                     (metric.Name.StartsWith("capture_file_frame_age_ms", StringComparison.Ordinal) ||
                      metric.Name.StartsWith("capture_sequence_frame_age_ms", StringComparison.Ordinal)) &&
                     metric.Count >= 30))
        {
            if (metric.P95 > 250.0)
            {
                throw new InvalidOperationException(
                    $"Settled diagnostic for {label} failed freshness age budget on {metric.Name}. p95={metric.P95:F3} ms.");
            }
        }

        foreach (var metric in report.Metrics.Where(metric =>
                     (metric.Name.StartsWith("capture_file_fresh_frame_ratio", StringComparison.Ordinal) ||
                      metric.Name.StartsWith("capture_sequence_fresh_frame_ratio", StringComparison.Ordinal)) &&
                     metric.Count >= 30))
        {
            if (metric.Average < 0.10)
            {
                throw new InvalidOperationException(
                    $"Settled diagnostic for {label} failed fresh-frame ratio on {metric.Name}. average={metric.Average:F3}.");
            }
        }
    }

    private static FrameProfileMetricReport RequireMetric(FrameProfileReport report, string name)
    {
        if (!TryGetMetric(report, name, out var metric))
        {
            throw new InvalidOperationException($"Required metric '{name}' was missing from the profile report.");
        }

        return metric;
    }

    private static bool TryGetMetric(FrameProfileReport report, string name, out FrameProfileMetricReport metric)
    {
        var found = report.Metrics.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        if (found == null)
        {
            metric = default!;
            return false;
        }

        metric = found;
        return true;
    }
}
