// Composites contain premultiplied BGRA uint, matching the shared image-effect backend.
cbuffer DatamoshParameters : register(b0)
{
    uint Width;
    uint Height;
    uint CellWidth;
    uint CellHeight;
    uint PassParity;
    uint SortAxis;
    uint Padding0;
    uint Padding1;
    float Feedback;
    float Displacement;
    uint FrameIndex;
    uint HasHistory;
};

Texture2D<uint4> History : register(t0);
Texture2D<uint4> CurrentFrame : register(t1);
RWTexture2D<uint4> Output : register(u0);

uint Hash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    return value ^ (value >> 16);
}

[numthreads(8, 8, 1)]
void DatamoshCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height) return;
    uint4 fresh = CurrentFrame.Load(int3(id.xy, 0));
    if (HasHistory == 0 || Feedback <= 0.0f)
    {
        Output[id.xy] = fresh;
        return;
    }

    uint2 block = id.xy / max(CellWidth, 1u);
    // Stable for six simulation steps: coherent block smears instead of pixel noise.
    uint seed = Hash(block.x + block.y * 65537u + (FrameIndex / 6u) * 31337u);
    float2 direction = float2((int)(seed & 255u) - 127, (int)((seed >> 8) & 255u) - 127) / 127.0f;
    float distance = Displacement * max(1.0f, min(Width, Height) * 0.08f);
    int2 previousPosition = clamp(int2(id.xy) - int2(round(direction * distance)),
        int2(0, 0), int2(Width - 1u, Height - 1u));
    float4 previous = float4(History.Load(int3(previousPosition, 0))) / 255.0f;
    float4 current = float4(fresh) / 255.0f;
    // Some blocks refresh sooner, producing torn patches within the temporal trail.
    float retention = Feedback * (((seed >> 24) & 7u) == 0u ? 0.25f : 1.0f);
    // RGB already carries coverage. Blend all four channels together so fading
    // trails stay correctly premultiplied when composited over another layer.
    Output[id.xy] = (uint4)round(saturate(lerp(current, previous, retention)) * 255.0f);
}
