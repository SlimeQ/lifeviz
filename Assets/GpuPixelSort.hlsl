cbuffer PixelSortParameters : register(b0)
{
    uint Width;
    uint Height;
    uint CellWidth;
    uint CellHeight;
    uint PassParity;
    uint SortAxis;
    uint Padding0;
    uint Padding1;
};

Texture2D<float4> CompositeInput : register(t0);
Texture2D<uint4> SortInput : register(t0);
Texture2D<uint4> PublishInput : register(t0);
RWTexture2D<uint4> SortOutput : register(u0);
RWTexture2D<unorm float4> PublishOutput : register(u0);
RWTexture2D<uint4> PublishPresentationOutput : register(u0);

float3 DecodeLogicalRgb(uint4 bgra)
{
    return float3(bgra.z, bgra.y, bgra.x) / 255.0f;
}

float Luma(uint4 bgra)
{
    return dot(DecodeLogicalRgb(bgra), float3(0.299f, 0.587f, 0.114f));
}

uint LumaKey(uint4 bgra)
{
    return (299u * bgra.z) + (587u * bgra.y) + (114u * bgra.x);
}

bool SortsBefore(uint4 left, uint leftIndex, uint4 right, uint rightIndex)
{
    uint leftLuma = LumaKey(left);
    uint rightLuma = LumaKey(right);
    if (leftLuma != rightLuma)
    {
        return leftLuma < rightLuma;
    }

    if (left.z != right.z)
    {
        return left.z < right.z;
    }

    if (left.y != right.y)
    {
        return left.y < right.y;
    }

    if (left.x != right.x)
    {
        return left.x < right.x;
    }

    if (left.w != right.w)
    {
        return left.w < right.w;
    }

    return leftIndex < rightIndex;
}

uint4 EncodeBgra(float4 logicalRgba)
{
    return uint4(
        (uint)round(saturate(logicalRgba.b) * 255.0f),
        (uint)round(saturate(logicalRgba.g) * 255.0f),
        (uint)round(saturate(logicalRgba.r) * 255.0f),
        (uint)round(saturate(logicalRgba.a) * 255.0f));
}

[numthreads(8, 8, 1)]
void InjectCompositeCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    SortOutput[dispatchThreadId.xy] = EncodeBgra(CompositeInput.Load(int3(dispatchThreadId.xy, 0)));
}

[numthreads(1, 1, 1)]
void SortPassCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint normalizedCellWidth = max(CellWidth, 1u);
    uint normalizedCellHeight = max(CellHeight, 1u);
    uint gridColumns = max(1u, (Width + normalizedCellWidth - 1u) / normalizedCellWidth);
    uint gridRows = max(1u, (Height + normalizedCellHeight - 1u) / normalizedCellHeight);

    if (dispatchThreadId.x >= gridColumns || dispatchThreadId.y >= gridRows)
    {
        return;
    }

    uint cellX = dispatchThreadId.x;
    uint cellY = dispatchThreadId.y;
    uint startX = cellX * normalizedCellWidth;
    uint endX = min(startX + normalizedCellWidth, Width);
    uint startY = cellY * normalizedCellHeight;
    uint endY = min(startY + normalizedCellHeight, Height);
    uint cellWidth = max(1u, endX - startX);
    uint cellHeight = max(1u, endY - startY);
    uint cellLength = cellWidth * cellHeight;

    for (uint localIndex = 0u; localIndex < cellLength; localIndex++)
    {
        uint sourceX = startX + (localIndex % cellWidth);
        uint sourceY = startY + (localIndex / cellWidth);
        uint4 currentValue = SortInput.Load(int3(int2(sourceX, sourceY), 0));

        uint rank = 0u;
        for (uint otherIndex = 0u; otherIndex < cellLength; otherIndex++)
        {
            uint otherX = startX + (otherIndex % cellWidth);
            uint otherY = startY + (otherIndex / cellWidth);
            uint4 otherValue = SortInput.Load(int3(int2(otherX, otherY), 0));
            if (SortsBefore(otherValue, otherIndex, currentValue, localIndex))
            {
                rank++;
            }
        }

        uint destinationX = startX + (rank % cellWidth);
        uint destinationY = startY + (rank / cellWidth);
        SortOutput[int2(destinationX, destinationY)] = currentValue;
    }
}

[numthreads(8, 8, 1)]
void PublishOutputCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint4 bgra = PublishInput.Load(int3(dispatchThreadId.xy, 0));
    PublishOutput[dispatchThreadId.xy] = float4(bgra.z, bgra.y, bgra.x, bgra.w) / 255.0f;
}

[numthreads(8, 8, 1)]
void PublishPresentationOutputCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= Width || dispatchThreadId.y >= Height)
    {
        return;
    }

    uint4 bgra = PublishInput.Load(int3(dispatchThreadId.xy, 0));
    PublishPresentationOutput[dispatchThreadId.xy] = uint4(bgra.z, bgra.y, bgra.x, bgra.w);
}
