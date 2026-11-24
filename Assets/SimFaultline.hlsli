#ifndef LIFEVIZ_SIM_FAULTLINE
#define LIFEVIZ_SIM_FAULTLINE

float2 FaultlinePlateJitter(int2 cell)
{
    // Integer hash gives a stable plate layout across live and offline renders.
    uint code = asuint(cell.x) * 1597334677u ^ asuint(cell.y) * 3812015801u;
    code ^= code >> 16;
    code *= 2246822519u;
    code ^= code >> 13;
    return (float2(code & 65535u, code >> 16) * (1.0 / 65535.0) - 0.5) * 0.58;
}

float4 SimFaultline(int2 pixel, float2 uv, float motionTime, float kick, float snare,
                    float highs, float slam, float held)
{
    float impact = saturate(max(kick, slam));
    float4 original = S(pixel);
    if (impact <= 0.008) return original;

    // Work in units of equal-sized plates, irrespective of the canvas aspect.
    float2 dimensions = max(float2(Width, Height), 1.0);
    float2 core = float2(0.37, 0.52);
    float2 plateScale = dimensions * (5.8 / min(dimensions.x, dimensions.y));
    float2 grid = uv * plateScale;
    int2 cell = int2(floor(grid));
    float2 local = frac(grid);
    float firstDistance = 100.0;
    float secondDistance = 100.0;
    float2 nearest = 0.0;
    float2 runnerUp = 0.0;
    float2 plateNoise = 0.0;
    [unroll] for (int y = -1; y <= 1; ++y)
    {
        [unroll] for (int x = -1; x <= 1; ++x)
        {
            int2 offset = int2(x, y);
            float2 jitter = FaultlinePlateJitter(cell + offset);
            float2 seed = float2(offset) + 0.5 + jitter;
            float2 v = seed - local;
            float d = dot(v, v);
            if (d < firstDistance)
            {
                runnerUp = nearest;
                secondDistance = firstDistance;
                nearest = seed;
                firstDistance = d;
                plateNoise = jitter;
            }
            else if (d < secondDistance)
            {
                runnerUp = seed;
                secondDistance = d;
            }
        }
    }

    // Near an edge, the difference of squared distances measures the distance
    // to the perpendicular bisector. That keeps both sides of a crack aligned.
    float seam = (secondDistance - firstDistance) * 0.5 / max(length(runnerUp - nearest), 0.05);
    float2 epicenterOffset = uv - core;
    float radius = length(epicenterOffset);
    // Kick envelope falls after its onset: the break starts locally, then
    // propagates while seam width decays. Slam seeds across the entire plate.
    float frontRadius = lerp(0.21, 1.06, saturate((1.0 - kick) * 1.9));
    float wave = 1.0 - smoothstep(frontRadius - 0.07, frontRadius + 0.06, radius);
    float pressure = saturate(max(pow(saturate(kick), 0.62) * wave, slam));
    float fracture = smoothstep(0.01, 0.23, pressure);
    float seamWidth = lerp(0.014, 0.135, saturate(pressure * 0.85 + slam * 0.28));
    float aa = clamp(max(plateScale.x / dimensions.x, plateScale.y / dimensions.y), 0.006, 0.055);
    float gap = (1.0 - smoothstep(seamWidth - aa * 0.65, seamWidth + aa, seam)) * fracture;

    float2 centerUv = (float2(cell) + nearest) / plateScale;
    float2 direction = centerUv - core + plateNoise * 0.28;
    direction *= rsqrt(max(dot(direction, direction), 0.006));
    float2 peel = (direction * (0.16 + slam * 0.27) + plateNoise * 0.42) * pressure;
    float2 tilt = float2(nearest.y - local.y, local.x - nearest.x) * (plateNoise.x * pressure);
    float4 plate = S(int2(float2(pixel) - (peel + tilt) / plateScale * dimensions + 0.5));

    // Broken rims are thin, colored and tied to coverage; no full-frame flash.
    float rim = (1.0 - smoothstep(seamWidth + aa, seamWidth + aa + 0.055, seam)) * (1.0 - gap);
    float3 amberBgra = float3(0.09, 0.46, 0.88);
    float face = saturate(0.63 + (nearest.y - local.y) * 0.28 + plateNoise.y * 0.45);
    float3 shade = lerp(plate.rgb, plate.rgb * face + plate.a * float3(0.055, 0.10, 0.17), pressure * 0.86);
    shade += plate.a * amberBgra * (rim * fracture * (0.31 + 0.17 * saturate(snare + highs)));
    float4 cracked = float4(min(shade, plate.a), plate.a);
    // Black negative space inside the original sheet, but never paint opaque
    // where the source group had no coverage merely to draw a crack.
    return lerp(lerp(original, cracked, fracture), float4(0, 0, 0, original.a), gap);
}

#endif
