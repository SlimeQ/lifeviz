Texture2D SimTexture : register(t0);
Texture2D OverlayTexture : register(t1);
SamplerState PointSampler : register(s0);

cbuffer CompositeParams : register(b0)
{
    int UseOverlay;
    int BlendMode;
    float Padding0;
    float Padding1;
};

struct VSOut
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

VSOut VSMain(uint vertexId : SV_VertexID)
{
    VSOut output;

    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0, 3.0),
        float2(3.0, -1.0)
    };

    float2 texCoords[3] =
    {
        float2(0.0, 1.0),
        float2(0.0, -1.0),
        float2(2.0, 1.0)
    };

    output.Position = float4(positions[vertexId], 0.0, 1.0);
    output.TexCoord = texCoords[vertexId];
    return output;
}

float3 Blend(float3 dst, float3 src, float srcAlpha, int mode)
{
    if (mode == 0)
    {
        return saturate(dst + src);
    }

    if (mode == 1)
    {
        return lerp(dst, src, srcAlpha);
    }

    if (mode == 2)
    {
        return dst * src;
    }

    if (mode == 3)
    {
        return 1.0 - ((1.0 - dst) * (1.0 - src));
    }

    if (mode == 4)
    {
        float3 low = 2.0 * dst * src;
        float3 high = 1.0 - (2.0 * (1.0 - dst) * (1.0 - src));
        return lerp(low, high, step(0.5, dst));
    }

    if (mode == 5)
    {
        return max(dst, src);
    }

    if (mode == 6)
    {
        return min(dst, src);
    }

    if (mode == 7)
    {
        return saturate(dst - src);
    }

    return src;
}

float4 PSMain(VSOut input) : SV_Target
{
    float4 sim = SimTexture.Sample(PointSampler, input.TexCoord);
    if (UseOverlay == 0)
    {
        return float4(sim.rgb, 1.0);
    }

    float4 overlay = OverlayTexture.Sample(PointSampler, input.TexCoord);
    float3 blended = Blend(sim.rgb, overlay.rgb, overlay.a, BlendMode);
    return float4(blended, 1.0);
}
