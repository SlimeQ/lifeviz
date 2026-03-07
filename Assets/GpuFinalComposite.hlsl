Texture2D<uint4> LayerTexture0 : register(t0);
Texture2D<uint4> LayerTexture1 : register(t1);
Texture2D<uint4> LayerTexture2 : register(t2);
Texture2D<uint4> LayerTexture3 : register(t3);
Texture2D<uint4> LayerTexture4 : register(t4);
Texture2D<uint4> LayerTexture5 : register(t5);
Texture2D<uint4> LayerTexture6 : register(t6);
Texture2D<uint4> LayerTexture7 : register(t7);
Texture2D UnderlayTexture : register(t8);
SamplerState PointSampler : register(s0);

cbuffer FinalCompositeParams : register(b0)
{
    int LayerCount;
    int UseUnderlay;
    int UseSignedAddSubPassthrough;
    int UseMixedAddSubPassthrough;

    int InvertComposite;
    float SimulationBaseline;
    float SurfaceWidth;
    float SurfaceHeight;

    int BlendMode0;
    int BlendMode1;
    int BlendMode2;
    int BlendMode3;

    int BlendMode4;
    int BlendMode5;
    int BlendMode6;
    int BlendMode7;

    float Opacity0;
    float Opacity1;
    float Opacity2;
    float Opacity3;

    float Opacity4;
    float Opacity5;
    float Opacity6;
    float Opacity7;

    float HueCos0;
    float HueCos1;
    float HueCos2;
    float HueCos3;

    float HueCos4;
    float HueCos5;
    float HueCos6;
    float HueCos7;

    float HueSin0;
    float HueSin1;
    float HueSin2;
    float HueSin3;

    float HueSin4;
    float HueSin5;
    float HueSin6;
    float HueSin7;
};

struct VSOut
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

float4 SampleLayer(int index, float2 uv)
{
    int2 coord = int2(
        clamp((int)(uv.x * SurfaceWidth), 0, (int)SurfaceWidth - 1),
        clamp((int)(uv.y * SurfaceHeight), 0, (int)SurfaceHeight - 1));
    float4 color = float4(0.0, 0.0, 0.0, 1.0);

    if (index == 0) color = float4(LayerTexture0.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 1) color = float4(LayerTexture1.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 2) color = float4(LayerTexture2.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 3) color = float4(LayerTexture3.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 4) color = float4(LayerTexture4.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 5) color = float4(LayerTexture5.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 6) color = float4(LayerTexture6.Load(int3(coord, 0)).rgb / 255.0, 1.0);
    else if (index == 7) color = float4(LayerTexture7.Load(int3(coord, 0)).rgb / 255.0, 1.0);

    return color;
}

int GetBlendMode(int index)
{
    if (index == 0) return BlendMode0;
    if (index == 1) return BlendMode1;
    if (index == 2) return BlendMode2;
    if (index == 3) return BlendMode3;
    if (index == 4) return BlendMode4;
    if (index == 5) return BlendMode5;
    if (index == 6) return BlendMode6;
    return BlendMode7;
}

float GetOpacity(int index)
{
    if (index == 0) return Opacity0;
    if (index == 1) return Opacity1;
    if (index == 2) return Opacity2;
    if (index == 3) return Opacity3;
    if (index == 4) return Opacity4;
    if (index == 5) return Opacity5;
    if (index == 6) return Opacity6;
    return Opacity7;
}

float GetHueCos(int index)
{
    if (index == 0) return HueCos0;
    if (index == 1) return HueCos1;
    if (index == 2) return HueCos2;
    if (index == 3) return HueCos3;
    if (index == 4) return HueCos4;
    if (index == 5) return HueCos5;
    if (index == 6) return HueCos6;
    return HueCos7;
}

float GetHueSin(int index)
{
    if (index == 0) return HueSin0;
    if (index == 1) return HueSin1;
    if (index == 2) return HueSin2;
    if (index == 3) return HueSin3;
    if (index == 4) return HueSin4;
    if (index == 5) return HueSin5;
    if (index == 6) return HueSin6;
    return HueSin7;
}

float3 ApplyHueShift(int index, float3 color)
{
    float cosValue = GetHueCos(index);
    float sinValue = GetHueSin(index);
    if (abs(cosValue - 1.0) <= 0.0001 && abs(sinValue) <= 0.0001)
    {
        return color;
    }

    float rr = 0.299 + (0.701 * cosValue) + (0.168 * sinValue);
    float rg = 0.587 - (0.587 * cosValue) + (0.330 * sinValue);
    float rb = 0.114 - (0.114 * cosValue) - (0.497 * sinValue);

    float gr = 0.299 - (0.299 * cosValue) - (0.328 * sinValue);
    float gg = 0.587 + (0.413 * cosValue) + (0.035 * sinValue);
    float gb = 0.114 - (0.114 * cosValue) + (0.292 * sinValue);

    float br = 0.299 - (0.300 * cosValue) + (1.250 * sinValue);
    float bg = 0.587 - (0.588 * cosValue) - (1.050 * sinValue);
    float bb = 0.114 + (0.886 * cosValue) - (0.203 * sinValue);

    float3 shifted;
    shifted.r = (rr * color.r) + (rg * color.g) + (rb * color.b);
    shifted.g = (gr * color.r) + (gg * color.g) + (gb * color.b);
    shifted.b = (br * color.r) + (bg * color.g) + (bb * color.b);
    return saturate(shifted);
}

float3 BlendSimulation(float3 dst, float3 src, int mode, float opacity)
{
    float3 blended;

    if (mode == 0)
    {
        blended = dst + src;
    }
    else if (mode == 2)
    {
        blended = dst * src;
    }
    else if (mode == 3)
    {
        blended = 1.0 - ((1.0 - dst) * (1.0 - src));
    }
    else if (mode == 4)
    {
        float3 low = 2.0 * dst * src;
        float3 high = 1.0 - (2.0 * (1.0 - dst) * (1.0 - src));
        blended = lerp(low, high, step(0.5, dst));
    }
    else if (mode == 5)
    {
        blended = max(dst, src);
    }
    else if (mode == 6)
    {
        blended = min(dst, src);
    }
    else if (mode == 7)
    {
        blended = dst - src;
    }
    else
    {
        blended = src;
    }

    return dst + ((blended - dst) * opacity);
}

float4 PSMain(VSOut input) : SV_Target
{
    float3 baseline = float3(SimulationBaseline, SimulationBaseline, SimulationBaseline);
    float3 underlay = baseline;
    if (UseUnderlay != 0)
    {
        underlay = UnderlayTexture.Sample(PointSampler, input.TexCoord).rgb;
    }

    float3 outputColor;

    if (UseSignedAddSubPassthrough != 0)
    {
        float3 additive = 0.0;
        float3 subtractive = 0.0;

        [unroll]
        for (int i = 0; i < 8; i++)
        {
            if (i >= LayerCount)
            {
                break;
            }

            float opacity = GetOpacity(i);
            if (opacity <= 0.0001)
            {
                continue;
            }

            float3 layerColor = ApplyHueShift(i, SampleLayer(i, input.TexCoord).rgb);
            int mode = GetBlendMode(i);
            if (mode == 7)
            {
                subtractive += (1.0 - layerColor) * opacity;
            }
            else
            {
                additive += layerColor * opacity;
            }
        }

        if (UseMixedAddSubPassthrough != 0)
        {
            float3 scaledSubtractive = subtractive * underlay;
            float3 scaledAdditive = additive * (1.0 - underlay);
            outputColor = underlay + scaledAdditive - scaledSubtractive;
        }
        else
        {
            outputColor = underlay + additive - subtractive;
        }
    }
    else
    {
        float3 simulation = baseline;

        [unroll]
        for (int i = 0; i < 8; i++)
        {
            if (i >= LayerCount)
            {
                break;
            }

            float opacity = GetOpacity(i);
            if (opacity <= 0.0001)
            {
                continue;
            }

            float3 layerColor = ApplyHueShift(i, SampleLayer(i, input.TexCoord).rgb);
            simulation = BlendSimulation(simulation, layerColor, GetBlendMode(i), opacity);
        }

        if (UseUnderlay != 0)
        {
            outputColor = underlay + (simulation - baseline);
        }
        else
        {
            outputColor = simulation;
        }
    }

    outputColor = saturate(outputColor);
    if (InvertComposite != 0)
    {
        outputColor = 1.0 - outputColor;
    }

    return float4(outputColor, 1.0);
}
