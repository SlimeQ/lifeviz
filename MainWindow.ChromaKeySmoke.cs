using System;
using System.Collections.Generic;

namespace lifeviz;

public partial class MainWindow
{
    internal (bool ok, string detail) RunGpuChromaKeyColorSmoke()
    {
        ResetGpuSourceCompositeSmokeCounters();

        bool blueOk = RunGpuChromaKeySample(
            sourceB: 0xFF,
            sourceG: 0x00,
            sourceR: 0x00,
            keyR: 0x00,
            keyG: 0x00,
            keyB: 0xFF);
        bool redOk = RunGpuChromaKeySample(
            sourceB: 0x00,
            sourceG: 0x00,
            sourceR: 0xFF,
            keyR: 0xFF,
            keyG: 0x00,
            keyB: 0x00);
        bool redPreservedByBlueKey = RunGpuChromaKeySample(
            sourceB: 0x00,
            sourceG: 0x00,
            sourceR: 0xFF,
            keyR: 0x00,
            keyG: 0x00,
            keyB: 0xFF,
            expectKeyed: false);
        bool greenOk = RunGpuChromaKeySample(
            sourceB: 0x00,
            sourceG: 0xFF,
            sourceR: 0x00,
            keyR: 0x00,
            keyG: 0xFF,
            keyB: 0x00);
        bool gpuPathOk = GetGpuSourceCompositePassCount() >= 8;

        bool ok = gpuPathOk && blueOk && redOk && redPreservedByBlueKey && greenOk;
        string detail = $"gpu={gpuPathOk}, blue={blueOk}, red={redOk}, " +
                        $"redWithBlueKey={redPreservedByBlueKey}, green={greenOk}";
        Logger.Info($"GPU chroma-key color smoke: {detail}.");
        return (ok, detail);
    }

    private bool RunGpuChromaKeySample(
        byte sourceB,
        byte sourceG,
        byte sourceR,
        byte keyR,
        byte keyG,
        byte keyB,
        bool expectKeyed = true)
    {
        const byte underlayB = 0x21;
        const byte underlayG = 0x43;
        const byte underlayR = 0x65;

        var underlay = CaptureSource.CreateFile("chroma-underlay", "Chroma Underlay", 1, 1);
        underlay.LastFrame = new SourceFrame(
            new[] { underlayB, underlayG, underlayR, (byte)255 },
            1,
            1,
            null,
            1,
            1);
        underlay.BlendMode = BlendMode.Normal;
        underlay.FitMode = FitMode.Stretch;

        var keyed = CaptureSource.CreateFile("chroma-keyed", "Chroma Keyed", 1, 1);
        keyed.LastFrame = new SourceFrame(
            new[] { sourceB, sourceG, sourceR, (byte)255 },
            1,
            1,
            null,
            1,
            1);
        keyed.BlendMode = BlendMode.Normal;
        keyed.FitMode = FitMode.Stretch;
        keyed.Opacity = 1.0;
        keyed.KeyEnabled = true;
        keyed.KeyColorR = keyR;
        keyed.KeyColorG = keyG;
        keyed.KeyColorB = keyB;
        keyed.KeyTolerance = 0.0;

        byte[]? buffer = null;
        var composite = BuildCompositeFrame(
            new List<CaptureSource> { underlay, keyed },
            ref buffer,
            useEngineDimensions: true,
            animationTime: 0.0,
            includeCpuReadback: true);
        if (composite == null || composite.Downscaled.Length < 4)
        {
            return false;
        }

        int center = (((composite.DownscaledHeight / 2) * composite.DownscaledWidth) +
                      (composite.DownscaledWidth / 2)) * 4;
        byte expectedB = expectKeyed ? underlayB : sourceB;
        byte expectedG = expectKeyed ? underlayG : sourceG;
        byte expectedR = expectKeyed ? underlayR : sourceR;
        return composite.Downscaled[center] == expectedB &&
               composite.Downscaled[center + 1] == expectedG &&
               composite.Downscaled[center + 2] == expectedR &&
               composite.Downscaled[center + 3] == 255;
    }
}
