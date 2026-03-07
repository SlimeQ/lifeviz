Texture2DArray<uint> HistoryInput : register(t0);
Texture2D<uint> InjectionMask : register(t1);
Texture2D<float4> CompositeInput : register(t1);
Texture2D<uint4> RgbInjectionMask : register(t2);
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
    uint LifeMode;
    float InjectHueRr;
    float InjectHueRg;
    float InjectHueRb;
    float InjectHueGr;
    float InjectHueGg;
    float InjectHueGb;
    float InjectHueBr;
    float InjectHueBg;
    float InjectHueBb;
    float Padding0;
    float Padding1;
    float Padding2;
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

bool PulseWidthAlivePeriod(float value, uint period)
{
    period = max(period, 1u);
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

bool PulseWidthAlive(float value)
{
    return PulseWidthAlivePeriod(value, PulsePeriod);
}

float3 ApplyInjectHueRotation(float3 value)
{
    return float3(
        Clamp01((InjectHueRr * value.r) + (InjectHueRg * value.g) + (InjectHueRb * value.b)),
        Clamp01((InjectHueGr * value.r) + (InjectHueGg * value.g) + (InjectHueGb * value.b)),
        Clamp01((InjectHueBr * value.r) + (InjectHueBg * value.g) + (InjectHueBb * value.b)));
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

float3 BuildCompositeRgbInput(int2 coord)
{
    float3 sample = CompositeInput.Load(int3(coord, 0)).rgb;
    if (InvertInput > 0.5)
    {
        sample = 1.0 - sample;
    }

    return ApplyInjectHueRotation(sample);
}

bool EvaluateInjectedAlive(float value, uint period, float randomGate)
{
    bool alive = EvaluateThresholdValue(value);
    if (InjectionMode == 1u)
    {
        alive = randomGate < MapIntensityThroughThresholdWindow(value);
    }
    else if (InjectionMode == 2u)
    {
        alive = PulseWidthAlivePeriod(MapIntensityThroughThresholdWindow(value), period);
    }
    return alive;
}

uint ReadHistory(int2 coord, uint slice)
{
    return HistoryInput.Load(int4(coord, slice, 0)).r;
}

uint CountNeighborsAt(int2 coord, uint slice)
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

            count += ReadHistory(sample, slice) != 0 ? 1u : 0u;
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
    for (uint slice = 1u; slice < Depth; slice++)
    {
        HistoryOutput[uint3(id.xy, slice)] = ReadHistory(coord, slice) != 0 ? 1u : 0u;
    }
}

[numthreads(8, 8, 1)]
void InjectRgbCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
    {
        return;
    }

    int2 coord = int2(id.xy);
    uint4 maskValue = RgbInjectionMask.Load(int3(coord, 0));
    uint rMask = maskValue.r != 0 ? 1u : 0u;
    uint gMask = maskValue.g != 0 ? 1u : 0u;
    uint bMask = maskValue.b != 0 ? 1u : 0u;

    uint rStart = 0u;
    uint gStart = RDepth;
    uint bStart = RDepth + GDepth;

    HistoryOutput[uint3(id.xy, rStart)] = max(ReadHistory(coord, rStart), rMask);
    [loop]
    for (uint rSlice = 1u; rSlice < RDepth; rSlice++)
    {
        HistoryOutput[uint3(id.xy, rStart + rSlice)] = ReadHistory(coord, rStart + rSlice) != 0 ? 1u : 0u;
    }

    HistoryOutput[uint3(id.xy, gStart)] = max(ReadHistory(coord, gStart), gMask);
    [loop]
    for (uint gSlice = 1u; gSlice < GDepth; gSlice++)
    {
        HistoryOutput[uint3(id.xy, gStart + gSlice)] = ReadHistory(coord, gStart + gSlice) != 0 ? 1u : 0u;
    }

    HistoryOutput[uint3(id.xy, bStart)] = max(ReadHistory(coord, bStart), bMask);
    [loop]
    for (uint bSlice = 1u; bSlice < BDepth; bSlice++)
    {
        HistoryOutput[uint3(id.xy, bStart + bSlice)] = ReadHistory(coord, bStart + bSlice) != 0 ? 1u : 0u;
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
    if (LifeMode == 0u)
    {
        uint mask = BuildCompositeMask(coord);
        uint current = ReadHistory(coord, 0) != 0 ? 1u : 0u;
        HistoryOutput[uint3(id.xy, 0)] = max(current, mask);

        [loop]
        for (uint slice = 1u; slice < Depth; slice++)
        {
            HistoryOutput[uint3(id.xy, slice)] = ReadHistory(coord, slice) != 0 ? 1u : 0u;
        }
        return;
    }

    float3 inputValue = BuildCompositeRgbInput(coord);
    float randomGate = Random01(uint2(coord), PulseStep * 1664525u + 1013904223u);
    bool noiseFail = NoiseProbability > 0.0 &&
        Random01(uint2(coord.y, coord.x), PulseStep * 22695477u + 1u) < NoiseProbability;

    uint rStart = 0u;
    uint gStart = RDepth;
    uint bStart = RDepth + GDepth;

    uint rMask = (!noiseFail && EvaluateInjectedAlive(inputValue.r, RDepth, randomGate)) ? 1u : 0u;
    uint gMask = (!noiseFail && EvaluateInjectedAlive(inputValue.g, GDepth, randomGate)) ? 1u : 0u;
    uint bMask = (!noiseFail && EvaluateInjectedAlive(inputValue.b, BDepth, randomGate)) ? 1u : 0u;

    HistoryOutput[uint3(id.xy, rStart)] = max(ReadHistory(coord, rStart), rMask);
    [loop]
    for (uint rSlice = 1u; rSlice < RDepth; rSlice++)
    {
        HistoryOutput[uint3(id.xy, rStart + rSlice)] = ReadHistory(coord, rStart + rSlice) != 0 ? 1u : 0u;
    }

    HistoryOutput[uint3(id.xy, gStart)] = max(ReadHistory(coord, gStart), gMask);
    [loop]
    for (uint gSlice = 1u; gSlice < GDepth; gSlice++)
    {
        HistoryOutput[uint3(id.xy, gStart + gSlice)] = ReadHistory(coord, gStart + gSlice) != 0 ? 1u : 0u;
    }

    HistoryOutput[uint3(id.xy, bStart)] = max(ReadHistory(coord, bStart), bMask);
    [loop]
    for (uint bSlice = 1u; bSlice < BDepth; bSlice++)
    {
        HistoryOutput[uint3(id.xy, bStart + bSlice)] = ReadHistory(coord, bStart + bSlice) != 0 ? 1u : 0u;
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
    if (LifeMode == 0u)
    {
        uint neighbors = CountNeighborsAt(coord, 0u);
        uint alive = ReadHistory(coord, 0u) != 0 ? 1u : 0u;
        uint next = neighbors == 3u || (alive != 0 && neighbors == 2u) ? 1u : 0u;
        HistoryOutput[uint3(id.xy, 0)] = next;

        [loop]
        for (uint slice = 1u; slice < Depth; slice++)
        {
            HistoryOutput[uint3(id.xy, slice)] = ReadHistory(coord, slice - 1u) != 0 ? 1u : 0u;
        }
        return;
    }

    uint rStart = 0u;
    uint gStart = RDepth;
    uint bStart = RDepth + GDepth;

    uint rNeighbors = CountNeighborsAt(coord, rStart);
    uint rAlive = ReadHistory(coord, rStart) != 0 ? 1u : 0u;
    HistoryOutput[uint3(id.xy, rStart)] = (rNeighbors == 3u || (rAlive != 0 && rNeighbors == 2u)) ? 1u : 0u;
    [loop]
    for (uint rSlice = 1u; rSlice < RDepth; rSlice++)
    {
        HistoryOutput[uint3(id.xy, rStart + rSlice)] = ReadHistory(coord, rStart + rSlice - 1u) != 0 ? 1u : 0u;
    }

    uint gNeighbors = CountNeighborsAt(coord, gStart);
    uint gAlive = ReadHistory(coord, gStart) != 0 ? 1u : 0u;
    HistoryOutput[uint3(id.xy, gStart)] = (gNeighbors == 3u || (gAlive != 0 && gNeighbors == 2u)) ? 1u : 0u;
    [loop]
    for (uint gSlice = 1u; gSlice < GDepth; gSlice++)
    {
        HistoryOutput[uint3(id.xy, gStart + gSlice)] = ReadHistory(coord, gStart + gSlice - 1u) != 0 ? 1u : 0u;
    }

    uint bNeighbors = CountNeighborsAt(coord, bStart);
    uint bAlive = ReadHistory(coord, bStart) != 0 ? 1u : 0u;
    HistoryOutput[uint3(id.xy, bStart)] = (bNeighbors == 3u || (bAlive != 0 && bNeighbors == 2u)) ? 1u : 0u;
    [loop]
    for (uint bSlice = 1u; bSlice < BDepth; bSlice++)
    {
        HistoryOutput[uint3(id.xy, bStart + bSlice)] = ReadHistory(coord, bStart + bSlice - 1u) != 0 ? 1u : 0u;
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
    uint r = EvaluateSlice(coord, 0u, RDepth);
    uint g = EvaluateSlice(coord, RDepth, GDepth);
    uint b = EvaluateSlice(coord, RDepth + GDepth, BDepth);
    ColorOutput[id.xy] = uint4(r, g, b, 255u);
}
