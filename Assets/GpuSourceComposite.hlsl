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
static const uint FlagPremultiplied = 1u << 4;
static const uint FlagOpaqueInput = 1u << 5;
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

float ComputeKeyAlpha(float3 sourceRgb)
{
    if ((Flags & FlagKeyEnabled) == 0u)
    {
        return 1.0f;
    }

    // B8G8R8A8 describes memory layout; shader sampling exposes logical RGB.
    float3 delta = sourceRgb - float3(KeyR, KeyG, KeyB);
    float distance = length(delta * 255.0f) / MaxColorDistance;
    if (Tolerance <= 0.0f)
    {
        return distance <= 0.0f ? 0.0f : 1.0f;
    }

    // Keep the UI range useful for compressed chroma backgrounds without letting
    // a high setting reach distant dark colors such as black from a bright-green key.
    float effectiveTolerance = 0.01f + (0.50f * Tolerance * Tolerance);
    float featherStart = effectiveTolerance * 0.82f;
    float edge = saturate((distance - featherStart) / max(0.000001f, effectiveTolerance - featherStart));
    return edge * edge * (3.0f - (2.0f * edge));
}

float3 ApplyArtisticBlend(float3 backdropRgb, float3 sourceRgb)
{
    float3 blended = sourceRgb;
    if (BlendMode == 0u) // Additive
    {
        blended = backdropRgb + sourceRgb;
    }
    else if (BlendMode == 2u) // Multiply
    {
        blended = backdropRgb * sourceRgb;
    }
    else if (BlendMode == 3u) // Screen
    {
        blended = 1.0f - ((1.0f - backdropRgb) * (1.0f - sourceRgb));
    }
    else if (BlendMode == 4u) // Overlay
    {
        blended = lerp(2.0f * backdropRgb * sourceRgb,
                       1.0f - (2.0f * (1.0f - backdropRgb) * (1.0f - sourceRgb)),
                       step(0.5f, backdropRgb));
    }
    else if (BlendMode == 5u) // Lighten
    {
        blended = max(backdropRgb, sourceRgb);
    }
    else if (BlendMode == 6u) // Darken
    {
        blended = min(backdropRgb, sourceRgb);
    }
    else if (BlendMode == 7u) // Subtractive
    {
        blended = backdropRgb - sourceRgb;
    }

    return blended;
}

float4 BlendColors(float4 currentColor, float4 sourceColor)
{
    // Media arrives straight-alpha; resolved groups already contain premultiplied RGB.
    bool useAlpha = (Flags & FlagUseAlpha) != 0u;
    // GDI/other opaque capture paths do not guarantee meaningful alpha bytes.
    if ((Flags & FlagOpaqueInput) != 0u) sourceColor.a = 1.0f;
    if (useAlpha && (Flags & FlagPremultiplied) != 0u)
    {
        sourceColor.rgb = sourceColor.a > 0.0f ? sourceColor.rgb / sourceColor.a : 0.0f;
    }
    float4 result = float4(currentColor.rgb, 1.0f);
    if ((Flags & FlagFirstLayer) != 0u)
    {
        // There is no backdrop to modulate. In particular do not turn a
        // lone full-frame Multiply child into an opaque group texture.
        if (BlendMode == 2u)
        {
            result = float4(0.0f, 0.0f, 0.0f, 0.0f);
        }
        else
        {
            float keyAlpha = ComputeKeyAlpha(sourceColor.rgb);
            float alpha = useAlpha ? sourceColor.a : 1.0f;
            float effectiveOpacity = Opacity * keyAlpha * alpha;
            result = float4(sourceColor.rgb * effectiveOpacity, useAlpha ? effectiveOpacity : 1.0f);
        }
    }
    else if (BlendMode == 1u) // Normal
    {
        float keyAlpha = ComputeKeyAlpha(sourceColor.rgb);
        float alpha = (useAlpha ? sourceColor.a : 1.0f) * Opacity * keyAlpha;
        result = float4(lerp(currentColor.rgb, sourceColor.rgb, saturate(alpha)),
                        alpha + currentColor.a * (1.0f - alpha));
    }
    else if (BlendMode == 2u && useAlpha) // Multiply
    {
        // Modulate the premultiplied backdrop without changing its coverage.
        float amount = sourceColor.a * Opacity;
        result = float4(currentColor.rgb * lerp(1.0f, sourceColor.rgb, amount), currentColor.a);
    }
    else if (useAlpha)
    {
        // In an isolated Layer Group, preserve coverage for child artistic blends.
        // The blend formula applies only where both child and backdrop have alpha.
        float a = sourceColor.a * Opacity;
        float b = currentColor.a;
        float3 backdrop = b > 0.0f ? saturate(currentColor.rgb / b) : 0.0f;
        float3 overlap = saturate(ApplyArtisticBlend(backdrop, sourceColor.rgb));
        result = float4(currentColor.rgb * (1.0f - a)
                        + a * lerp(sourceColor.rgb, overlap, b),
                        a + b * (1.0f - a));
    }
    else
    {
        result = float4(saturate(lerp(currentColor.rgb, ApplyArtisticBlend(currentColor.rgb, sourceColor.rgb), Opacity)), 1.0f);
    }

    return result;
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
        return ((Flags & FlagFirstLayer) != 0u) ? float4(0.0f, 0.0f, 0.0f, (Flags & FlagUseAlpha) != 0u ? 0.0f : 1.0f) : currentColor;
    }

    if ((Flags & FlagMirror) != 0u)
    {
        samplePoint.x = (SourceWidth - 1.0f) - samplePoint.x;
    }

    float2 uv = float2((samplePoint.x + 0.5f) / SourceWidth, (samplePoint.y + 0.5f) / SourceHeight);
    float4 sourceColor = (FitMode == 4u)
        ? SourceTexture.SampleLevel(LinearWrapSampler, uv, 0.0f)
        : SourceTexture.SampleLevel(LinearClampSampler, uv, 0.0f);

    return BlendColors(currentColor, sourceColor);
}
