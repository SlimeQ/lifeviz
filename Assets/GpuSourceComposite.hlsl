Texture2D<float4> CurrentComposite : register(t0);
Texture2D<float4> SourceTexture : register(t1);
SamplerState LinearClampSampler : register(s0);
SamplerState LinearWrapSampler : register(s1);

cbuffer SourceCompositeParams : register(b0)
{
    float DestWidth;
    float DestHeight;
    float SourceWidth;
    float SourceHeight;

    float ScaleX;
    float ScaleY;
    float OffsetX;
    float OffsetY;

    float ScaledWidth;
    float ScaledHeight;
    float Opacity;
    float Tolerance;

    float M11;
    float M12;
    float M21;
    float M22;

    float TransformOffsetX;
    float TransformOffsetY;
    float KeyB;
    float KeyG;

    float KeyR;
    uint FitMode;
    uint BlendMode;
    uint Flags;
};

static const uint FlagMirror = 1u << 0;
static const uint FlagUseAlpha = 1u << 1;
static const uint FlagKeyEnabled = 1u << 2;
static const uint FlagFirstLayer = 1u << 3;
static const float MaxColorDistance = 441.6729559300637f;

struct PSInput
{
    float4 Position : SV_POSITION;
};

PSInput VSMain(uint vertexId : SV_VertexID)
{
    PSInput output;

    float2 position;
    if (vertexId == 0)
    {
        position = float2(-1.0f, -1.0f);
    }
    else if (vertexId == 1)
    {
        position = float2(-1.0f, 3.0f);
    }
    else
    {
        position = float2(3.0f, -1.0f);
    }

    output.Position = float4(position, 0.0f, 1.0f);
    return output;
}

bool TryMapSamplePoint(float2 destPoint, out float2 samplePoint)
{
    samplePoint = float2(0.0f, 0.0f);

    if (FitMode == 0u) // Fit
    {
        float2 inputPoint = float2(destPoint.x - OffsetX, destPoint.y - OffsetY);
        if (inputPoint.x < 0.0f || inputPoint.y < 0.0f || inputPoint.x >= ScaledWidth || inputPoint.y >= ScaledHeight)
        {
            return false;
        }

        samplePoint = float2((inputPoint.x / ScaleX) - 0.5f, (inputPoint.y / ScaleY) - 0.5f);
        return true;
    }

    if (FitMode == 1u) // Fill
    {
        float2 inputPoint = float2(destPoint.x + OffsetX, destPoint.y + OffsetY);
        samplePoint = float2((inputPoint.x / ScaleX) - 0.5f, (inputPoint.y / ScaleY) - 0.5f);
        return true;
    }

    if (FitMode == 2u) // Stretch
    {
        samplePoint = float2((destPoint.x * ScaleX) - 0.5f, (destPoint.y * ScaleY) - 0.5f);
        return true;
    }

    if (FitMode == 3u) // Center
    {
        float2 inputPoint = float2(destPoint.x - OffsetX, destPoint.y - OffsetY);
        if (inputPoint.x < 0.0f || inputPoint.y < 0.0f || inputPoint.x >= SourceWidth || inputPoint.y >= SourceHeight)
        {
            return false;
        }

        samplePoint = inputPoint - 0.5f;
        return true;
    }

    // Tile
    samplePoint = float2(frac(destPoint.x / SourceWidth) * SourceWidth - 0.5f,
                         frac(destPoint.y / SourceHeight) * SourceHeight - 0.5f);
    return true;
}

float ComputeKeyAlpha(float3 sourceBgr)
{
    if ((Flags & FlagKeyEnabled) == 0u)
    {
        return 1.0f;
    }

    float3 delta = sourceBgr - float3(KeyB, KeyG, KeyR);
    float distance = length(delta * 255.0f) / MaxColorDistance;
    if (Tolerance <= 0.0f)
    {
        return distance <= 0.0f ? 0.0f : 1.0f;
    }

    return saturate(distance / Tolerance);
}

float4 Blend(float4 currentColor, float4 sourceColor)
{
    if ((Flags & FlagFirstLayer) != 0u)
    {
        float keyAlpha = ComputeKeyAlpha(sourceColor.rgb);
        float alpha = ((Flags & FlagUseAlpha) != 0u) ? sourceColor.a : 1.0f;
        float effectiveOpacity = Opacity * keyAlpha * alpha;
        return float4(sourceColor.rgb * effectiveOpacity, 1.0f);
    }

    if (BlendMode == 1u) // Normal
    {
        float keyAlpha = ComputeKeyAlpha(sourceColor.rgb);
        float alpha = (((Flags & FlagUseAlpha) != 0u) ? sourceColor.a : 1.0f) * Opacity * keyAlpha;
        return float4(lerp(currentColor.rgb, sourceColor.rgb, saturate(alpha)), 1.0f);
    }

    float3 blended = sourceColor.rgb;
    if (BlendMode == 0u) // Additive
    {
        blended = currentColor.rgb + sourceColor.rgb;
    }
    else if (BlendMode == 2u) // Multiply
    {
        blended = currentColor.rgb * sourceColor.rgb;
    }
    else if (BlendMode == 3u) // Screen
    {
        blended = 1.0f - ((1.0f - currentColor.rgb) * (1.0f - sourceColor.rgb));
    }
    else if (BlendMode == 4u) // Overlay
    {
        blended = lerp(2.0f * currentColor.rgb * sourceColor.rgb,
                       1.0f - (2.0f * (1.0f - currentColor.rgb) * (1.0f - sourceColor.rgb)),
                       step(0.5f, currentColor.rgb));
    }
    else if (BlendMode == 5u) // Lighten
    {
        blended = max(currentColor.rgb, sourceColor.rgb);
    }
    else if (BlendMode == 6u) // Darken
    {
        blended = min(currentColor.rgb, sourceColor.rgb);
    }
    else if (BlendMode == 7u) // Subtractive
    {
        blended = currentColor.rgb - sourceColor.rgb;
    }

    return float4(saturate(lerp(currentColor.rgb, blended, Opacity)), 1.0f);
}

float4 PSMain(PSInput input) : SV_TARGET
{
    int2 pixelCoord = int2(input.Position.xy);
    float2 destPoint = input.Position.xy;
    float4 currentColor = CurrentComposite.Load(int3(pixelCoord, 0));

    float2 transformed = float2(
        (M11 * destPoint.x) + (M12 * destPoint.y) + TransformOffsetX,
        (M21 * destPoint.x) + (M22 * destPoint.y) + TransformOffsetY);

    float2 samplePoint;
    if (!TryMapSamplePoint(transformed, samplePoint))
    {
        return ((Flags & FlagFirstLayer) != 0u) ? float4(0.0f, 0.0f, 0.0f, 1.0f) : currentColor;
    }

    if ((Flags & FlagMirror) != 0u)
    {
        samplePoint.x = (SourceWidth - 1.0f) - samplePoint.x;
    }

    float2 uv = float2((samplePoint.x + 0.5f) / SourceWidth, (samplePoint.y + 0.5f) / SourceHeight);
    float4 sourceColor = (FitMode == 4u)
        ? SourceTexture.SampleLevel(LinearWrapSampler, uv, 0.0f)
        : SourceTexture.SampleLevel(LinearClampSampler, uv, 0.0f);

    return Blend(currentColor, sourceColor);
}
