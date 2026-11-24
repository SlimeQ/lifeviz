// Metaball lobes merge before holes punch through. Apertures carry separate
// coverage, so the effects beneath this group remain visible through them.
float InkTrapLobe(float2 p, float2 center, float radius)
{
    float2 d = p - center;
    float weight = saturate(1.0 - dot(d, d) / (radius * radius));
    return weight * weight;
}

float2 InkTrapAperture(float2 p, float2 center, float age, float radius, float aa, float phase)
{
    float2 d = (p - center) / max(radius, 0.0001);
    float2 z = float2(d.x + d.y * d.y * 0.18, d.y * 0.89 + d.x * 0.16);
    float2 z2 = z * z;
    // Cubic lobing stays defined at the center, unlike atan normalization.
    float curl = (z.x * z2.x - 3.0 * z.x * z2.y) * cos(phase)
               + (3.0 * z2.x * z.y - z.y * z2.y) * sin(phase);
    float edge = (length(z) - 1.0 + clamp(curl, -0.11, 0.11) * (0.45 + age * 0.55)) * radius;
    return float2(1.0 - smoothstep(-aa, aa, edge),
                  smoothstep(-aa, aa, edge) * (1.0 - smoothstep(0.005, 0.024, edge)));
}

float4 SimInkTrap(int2 pixel, float2 uv, float motionTime, float kick, float snare, float highs, float slam, float held)
{
    float2 scale = float2(Width, Height) / float(min(Width, Height));
    float2 p = (uv - 0.5) * scale;
    float s = min(scale.x, 1.8);
    float t = motionTime * 0.51;
    float age = 1.0 - saturate(kick);
    float pulse = saturate(kick * 1.3 + snare * 0.55);
    float stretch = 0.07 * sin(t * 0.7);
    float growth = 0.036 * sin(t * 0.4);
    float2 c0 = float2(-0.26 * s + 0.11 * sin(t), -0.15 + stretch);
    float2 c1 = float2( 0.28 * s + 0.09 * cos(t * 0.87), 0.09 - stretch);
    float2 c2 = float2(-0.14 * s + 0.12 * cos(t * 1.2), 0.24 - stretch);
    float2 c3 = float2( 0.23 * s + 0.08 * sin(t * 0.87), -0.23 + stretch);
    float2 c4 = float2( 0.08 * cos(t * 0.56), 0.09 * sin(t));
    float field = InkTrapLobe(p, c0, 0.40 + growth) + InkTrapLobe(p, c1, 0.45 - growth)
                + InkTrapLobe(p, c2, 0.39 + growth) + InkTrapLobe(p, c3, 0.38 - growth)
                + InkTrapLobe(p, c4, 0.37 + growth);
    float aa = max(1.25 / float(min(Width, Height)), 0.001);
    float threshold = 0.52 + 0.04 * sin(t * 0.38);
    float fill = smoothstep(threshold - aa * 4.0, threshold + aa * 4.0, field);
    // A transient creates real, moving negative space. A slow release shrinks
    // apertures while the edges curl; no global brightness or alpha pulse.
    float radius = 0.17 * pulse * (0.72 + age * 0.42);
    float2 aperture = 0;
    if (radius > aa * 3.0)
    {
        aperture = max(InkTrapAperture(p, lerp(c4, c0, 0.45), age, radius, aa, t + age),
                       InkTrapAperture(p, lerp(c4, c1, 0.58), age, radius * 0.79, aa, t - age));
    }
    float ink = fill * (1.0 - aperture.x);
    float edge = fill * max(aperture.y * 0.95, 1.0 - smoothstep(threshold, threshold + 0.115, field));

    float4 source = S(pixel);
    float3 tint = lerp(float3(0.75, 0.11, 0.35), float3(0.87, 0.45, 0.97), saturate(field * 0.43));
    tint = lerp(tint, source.rgb / max(source.a, 1.0 / 255.0), source.a * 0.25);
    tint = lerp(tint, float3(0.82, 0.94, 0.99), edge * (0.62 + 0.18 * highs));
    float4 positive = float4(saturate(tint) * ink, ink);

    // Crossfade into a dark mask over a warm field only on the manual slam.
    // Background coverage is deliberate for this inversion, not accidental
    // RGB in transparent pixels.
    float3 negative = lerp(float3(0.38, 0.81, 0.98), float3(0.045, 0.025, 0.035), ink);
    return lerp(positive, float4(negative, 1.0), saturate(slam) * 0.96);
}
