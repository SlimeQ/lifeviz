using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace lifeviz;

internal sealed class BlendEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("/Assets/Blend.ps", UriKind.Relative)
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(BlendEffect), 0);

    public BlendEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(OverlayProperty);
        UpdateShaderValue(ModeProperty);
        UpdateShaderValue(UseOverlayProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public static readonly DependencyProperty OverlayProperty =
        RegisterPixelShaderSamplerProperty(nameof(Overlay), typeof(BlendEffect), 1);

    public Brush Overlay
    {
        get => (Brush)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(double), typeof(BlendEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public double Mode
    {
        get => (double)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty UseOverlayProperty =
        DependencyProperty.Register(nameof(UseOverlay), typeof(double), typeof(BlendEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));

    public double UseOverlay
    {
        get => (double)GetValue(UseOverlayProperty);
        set => SetValue(UseOverlayProperty, value);
    }
}
