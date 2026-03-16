using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace lifeviz;

internal sealed class GpuSharedDevice
{
    private static readonly object InstanceLock = new();
    private static GpuSharedDevice? _instance;

    private GpuSharedDevice(ID3D11Device1 device, ID3D11DeviceContext1 context, FeatureLevel featureLevel)
    {
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
    }

    public ID3D11Device1 Device { get; }
    public ID3D11DeviceContext1 Context { get; }
    public FeatureLevel FeatureLevel { get; }
    public object SyncRoot { get; } = new();

    public static GpuSharedDevice GetOrCreate()
    {
        lock (InstanceLock)
        {
            _instance ??= Create();
            return _instance;
        }
    }

    public static void FlushIfCreated()
    {
        GpuSharedDevice? instance;
        lock (InstanceLock)
        {
            instance = _instance;
        }

        if (instance == null)
        {
            return;
        }

        lock (instance.SyncRoot)
        {
            instance.Context.Flush();
        }
    }

    private static GpuSharedDevice Create()
    {
        FeatureLevel[] primaryLevels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        };

        try
        {
            D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.None,
                primaryLevels,
                out ID3D11Device device,
                out FeatureLevel featureLevel,
                out ID3D11DeviceContext context);
            return Build(device, context, featureLevel);
        }
        catch
        {
            FeatureLevel[] fallbackLevels =
            {
                FeatureLevel.Level_11_0
            };

            D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.None,
                fallbackLevels,
                out ID3D11Device device,
                out FeatureLevel featureLevel,
                out ID3D11DeviceContext context);
            return Build(device, context, featureLevel);
        }
    }

    private static GpuSharedDevice Build(ID3D11Device device, ID3D11DeviceContext context, FeatureLevel featureLevel)
    {
        ID3D11Device1 device1 = device.QueryInterface<ID3D11Device1>();
        ID3D11DeviceContext1 context1 = context.QueryInterface<ID3D11DeviceContext1>();
        device.Dispose();
        context.Dispose();
        return new GpuSharedDevice(device1, context1, featureLevel);
    }
}

internal sealed class GpuCompositeSurface
{
    public GpuCompositeSurface(ID3D11Texture2D texture, ID3D11ShaderResourceView shaderResourceView, IntPtr sharedTextureHandle, int width, int height)
    {
        Texture = texture;
        ShaderResourceView = shaderResourceView;
        SharedTextureHandle = sharedTextureHandle;
        Width = width;
        Height = height;
    }

    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView ShaderResourceView { get; }
    public IntPtr SharedTextureHandle { get; }
    public int Width { get; }
    public int Height { get; }
}
