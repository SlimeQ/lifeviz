Texture2DArray<uint> HistoryInput : register(t0);
Texture2D<uint> InjectionMask : register(t1);
Texture2D<float4> CompositeInput : register(t1);
RWTexture2DArray<uint> HistoryOutput : register(u0);
RWTexture2D<uint4> ColorOutput : register(u0);

cbuffer SimulationParams : register(b0)
{
    uint Width;
    uint Height;
    uint Depth;
    uint BinningMode;
    uint RDepth;
    uint GDepth;
    uint BDepth;
    uint InjectionMode;
    float ThresholdMin;
    float ThresholdMax;
    float NoiseProbability;
    float InvertInput;
    uint PulsePeriod;
    uint PulseStep;
    uint InvertThreshold;
    uint Padding;
};

float Clamp01(float value)
{
    return clamp(value, 0.0, 1.0);
}

bool EvaluateThresholdValue(float value)
{
    float minValue = Clamp01(ThresholdMin);
    float maxValue = Clamp01(ThresholdMax);
    if (minValue > maxValue)
    {
        float swapValue = minValue;
        minValue = maxValue;
        maxValue = swapValue;
    }

    bool insideWindow = value >= minValue && value <= maxValue;
    return InvertThreshold != 0u ? !insideWindow : insideWindow;
}

float MapIntensityThroughThresholdWindow(float value)
{
    value = Clamp01(value);
    float minValue = Clamp01(ThresholdMin);
    float maxValue = Clamp01(ThresholdMax);
    if (minValue > maxValue)
    {
        float swapValue = minValue;
        minValue = maxValue;
        maxValue = swapValue;
    }

    const float Epsilon = 1e-6;
    if (InvertThreshold == 0u)
    {
        if ((maxValue - minValue) <= Epsilon)
        {
            return value >= maxValue ? 1.0 : 0.0;
        }

        return Clamp01((value - minValue) / (maxValue - minValue));
    }

    if ((maxValue - minValue) <= Epsilon)
    {
        return value < minValue || value > maxValue ? 1.0 : 0.0;
    }

    float lower = minValue <= Epsilon ? 0.0 : Clamp01((minValue - value) / minValue);
    float upper = (1.0 - maxValue) <= Epsilon ? 0.0 : Clamp01((value - maxValue) / (1.0 - maxValue));
    return max(lower, upper);
}

uint HashUint(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

float Random01(uint2 coord, uint seed)
{
    uint value = HashUint(coord.x ^ HashUint(coord.y ^ HashUint(seed)));
    return (value & 0x00ffffffu) / 16777215.0;
}

bool PulseWidthAlive(float value)
{
    uint period = max(PulsePeriod, 1u);
    uint aliveCount = (uint)round(Clamp01(value) * period);
    if (aliveCount == 0u)
    {
        return false;
    }

    if (aliveCount >= period)
    {
        return true;
    }

    uint phase = PulseStep % period;
    return ((phase * aliveCount) % period) < aliveCount;
}

uint BuildCompositeMask(int2 coord)
{
    float4 sample = CompositeInput.Load(int3(coord, 0));
    float luminance = dot(sample.rgb, float3(0.2126, 0.7152, 0.0722));
    if (InvertInput > 0.5)
    {
        luminance = 1.0 - luminance;
    }

    float injectionDrive = MapIntensityThroughThresholdWindow(luminance);
    bool alive = false;
    if (InjectionMode == 1u)
    {
        alive = Random01(uint2(coord), PulseStep * 1664525u + 1013904223u) < injectionDrive;
    }
    else if (InjectionMode == 2u)
    {
        alive = PulseWidthAlive(injectionDrive);
    }
    else
    {
        alive = EvaluateThresholdValue(luminance);
    }

    bool noiseFail = NoiseProbability > 0.0 &&
        Random01(uint2(coord.y, coord.x), PulseStep * 22695477u + 1u) < NoiseProbability;
    return (!noiseFail && alive) ? 1u : 0u;
}

uint ReadHistory(int2 coord, uint slice)
{
    return HistoryInput.Load(int4(coord, slice, 0)).r;
}

uint CountNeighbors(int2 coord)
{
    uint count = 0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0)
            {
                continue;
            }

            int2 sample = coord + int2(x, y);
            if (sample.x < 0 || sample.y < 0 || sample.x >= int(Width) || sample.y >= int(Height))
            {
                continue;
            }

            count += ReadHistory(sample, 0) != 0 ? 1 : 0;
        }
    }

    return count;
}

uint EvaluateSlice(int2 coord, uint sliceStart, uint sliceLength)
{
    if (sliceLength == 0)
    {
        return 0;
    }

    uint frames = min(sliceLength, Depth);
    if (frames == 0)
    {
        return 0;
    }

    if (BinningMode == 1)
    {
        uint value = 0;
        [loop]
        for (uint i = 0; i < frames; i++)
        {
            uint historyIndex = sliceStart + i;
            if (historyIndex >= Depth)
            {
                break;
            }

            if (ReadHistory(coord, historyIndex) != 0)
            {
                uint bit = frames - 1 - i;
                value |= 1u << bit;
            }
        }

        uint maxValue = (1u << frames) - 1u;
        return maxValue == 0 ? 0 : (value * 255u + (maxValue / 2u)) / maxValue;
    }

    uint alive = 0;
    uint considered = 0;
    [loop]
    for (uint i = 0; i < frames; i++)
    {
        uint historyIndex = sliceStart + i;
        if (historyIndex >= Depth)
        {
            break;
        }

        considered++;
        if (ReadHistory(coord, historyIndex) != 0)
        {
            alive++;
        }
    }

    return considered == 0 ? 0 : (alive * 255u + (considered / 2u)) / considered;
}

[numthreads(8, 8, 1)]
void InjectCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
    {
        return;
    }

    int2 coord = int2(id.xy);
    uint mask = InjectionMask.Load(int3(coord, 0)).r != 0 ? 1u : 0u;
    uint current = ReadHistory(coord, 0) != 0 ? 1u : 0u;
    HistoryOutput[uint3(id.xy, 0)] = max(current, mask);

    [loop]
    for (uint slice = 1; slice < Depth; slice++)
    {
        HistoryOutput[uint3(id.xy, slice)] = ReadHistory(coord, slice) != 0 ? 1u : 0u;
    }
}

[numthreads(8, 8, 1)]
void InjectCompositeCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
    {
        return;
    }

    int2 coord = int2(id.xy);
    uint mask = BuildCompositeMask(coord);
    uint current = ReadHistory(coord, 0) != 0 ? 1u : 0u;
    HistoryOutput[uint3(id.xy, 0)] = max(current, mask);

    [loop]
    for (uint slice = 1; slice < Depth; slice++)
    {
        HistoryOutput[uint3(id.xy, slice)] = ReadHistory(coord, slice) != 0 ? 1u : 0u;
    }
}

[numthreads(8, 8, 1)]
void StepCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
    {
        return;
    }

    int2 coord = int2(id.xy);
    uint neighbors = CountNeighbors(coord);
    uint alive = ReadHistory(coord, 0) != 0 ? 1u : 0u;
    uint next = neighbors == 3u || (alive != 0 && neighbors == 2u) ? 1u : 0u;
    HistoryOutput[uint3(id.xy, 0)] = next;

    [loop]
    for (uint slice = 1; slice < Depth; slice++)
    {
        HistoryOutput[uint3(id.xy, slice)] = ReadHistory(coord, slice - 1) != 0 ? 1u : 0u;
    }
}

[numthreads(8, 8, 1)]
void RenderCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
    {
        return;
    }

    int2 coord = int2(id.xy);
    uint r = EvaluateSlice(coord, 0, RDepth);
    uint g = EvaluateSlice(coord, RDepth, GDepth);
    uint b = EvaluateSlice(coord, RDepth + GDepth, BDepth);
    ColorOutput[id.xy] = uint4(r, g, b, 255u);
}
