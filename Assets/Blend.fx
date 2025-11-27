// Blend.fx
sampler2D inputSampler : register(s0);
sampler2D overlaySampler : register(s1);
float mode; // 0=Additive,1=Normal,2=Multiply,3=Screen,4=Overlay,5=Lighten,6=Darken,7=Subtractive
float useOverlay; // 0 or 1

float3 Blend(float3 baseColor, float3 overlayColor)
{
    if (useOverlay < 0.5) return baseColor;
    if (mode < 0.5) // Additive
    {
        return saturate(baseColor + overlayColor);
    }
    else if (mode < 1.5) // Normal
    {
        return overlayColor;
    }
    else if (mode < 2.5) // Multiply
    {
        return baseColor * overlayColor;
    }
    else if (mode < 3.5) // Screen
    {
        return 1 - ((1 - baseColor) * (1 - overlayColor));
    }
    else if (mode < 4.5) // Overlay
    {
        float3 branch1 = 2 * baseColor * overlayColor;
        float3 branch2 = 1 - 2 * (1 - baseColor) * (1 - overlayColor);
        float3 mask = step(0.5, baseColor);
        return lerp(branch1, branch2, mask);
    }
    else if (mode < 5.5) // Lighten
    {
        return max(baseColor, overlayColor);
    }
    else if (mode < 7.5) // Subtractive
    {
        return saturate(baseColor - overlayColor);
    }
    else // Darken
    {
        return min(baseColor, overlayColor);
    }
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 baseColor = tex2D(inputSampler, uv);
    float4 overlayColor = tex2D(overlaySampler, uv);
    float3 blended = Blend(baseColor.rgb, overlayColor.rgb);
    return float4(blended, 1);
}
