// Two orbital fields, with two rings of faceted shards each. Analytic coverage
// avoids per-pixel particle scans and maintains a crisp silhouette at any size.
float2 MagnetChoirRing(float angle, float radius, float spacing, float rotation,
                       float count, float ringRadius, float edge)
{
    float sector = frac(angle * count + rotation) - 0.5;
    // Adjacent facets are slightly staggered around the ring.
    float facetAngle = (angle + rotation / count) * 6.28318530718;
    float center = ringRadius * spacing * (1 + 0.08 * sin(facetAngle * 2));
    float flank = (radius - center) / (ringRadius * 0.28 + 0.008);
    float distanceToEdge = 1 - (abs(sector) * 2.75 + abs(flank));
    float coverage = smoothstep(-edge, edge, distanceToEdge);
    float bevel = smoothstep(0.03, 0.35, distanceToEdge);
    float light = saturate(0.28 + 0.45 * bevel + 0.22 * (flank > sector ? 1 : 0));
    return float2(coverage, light);
}

float4 MagnetChoirField(float2 shardPoint, float2 magnet, float motionTime, float spacing,
                        float highs, float slam, float edge, float2 dimension,
                        float3 pigment, float3 highlight, float handedness)
{
    float2 offset = shardPoint - magnet;
    float radius = length(offset);
    // Keep atan2 well-defined at the exact magnet center.
    float angle = atan2(offset.y, offset.x + (radius < 1e-7 ? 1e-7 : 0)) * (1.0 / 6.28318530718);
    float turn = motionTime * 0.09 * handedness;
    float2 inner = MagnetChoirRing(angle, radius, spacing, turn * 5, 5, 0.104, edge);
    float2 outer = MagnetChoirRing(angle, radius, spacing, -turn * 3 + 0.34, 7, 0.201, edge);
    float coverage = max(inner.x, outer.x);
    float3 color = 0;
    if (coverage > 0)
    {
        // Collect a displaced color swatch from the incoming image where it
        // exists; synthetic pigments keep bare/transparent fields usable.
        float2 uv = 0.5 + (magnet + offset * 0.66) / (dimension / min(dimension.x, dimension.y));
        float4 source = S(int2(saturate(uv) * (dimension - 1)));
        float light = inner.x >= outer.x ? inner.y : outer.y;
        color = lerp(pigment, highlight, light);
        color = lerp(color, source.rgb / max(source.a, 1.0 / 255), source.a * 0.37);
    }

    // Three short spark trajectories per magnet; hats change their size and
    // distance, not the exposure of the whole image.
    float spark = 0;
    if (highs > 0.025)
    {
        float phase = angle * 3 + turn * -2 + 0.21;
        float sector = frac(phase) - 0.5;
        float sparkRadius = 0.201 * spacing + 0.065 + highs * 0.069 + slam * 0.029;
        float shape = abs(sector) * 35 + abs(radius - sparkRadius) * 94;
        spark = (1 - smoothstep(0.6, 1.0, shape)) * saturate((highs - 0.025) * 2);
    }
    float4 body = float4(color * coverage, coverage);
    float4 fleck = float4(highlight * spark, spark);
    return fleck + body * (1 - fleck.a);
}

float4 SimMagnetChoir(int2 pixel, float2 uv, float motionTime, float kick,
                     float snare, float highs, float slam, float held)
{
    float2 dimension = float2(Width, Height);
    float2 aspect = dimension / min(dimension.x, dimension.y);
    float2 shardPoint = (uv - 0.5) * aspect;
    // Bass draws mass inward. Snare rotates the whole two-magnet formation
    // through almost 90 degrees, distinct from the bass contraction.
    float bass = saturate(kick);
    float breakUp = saturate(slam);
    float swap = saturate(snare);
    float drift = 0.13 * sin(motionTime * 0.46);
    float tilt = drift + swap * 1.52 + breakUp * 0.48;
    float separation = 0.176 - bass * 0.049 + breakUp * 0.049;
    float2 anchor = separation * float2(cos(tilt), sin(tilt));
    float spacing = 1.0 - 0.37 * bass + 0.43 * breakUp;
    float edge = max(0.016, 4.8 / min(dimension.x, dimension.y));

    // Color components are in BGR order, as is S().
    float4 left = MagnetChoirField(shardPoint, -anchor, motionTime, spacing, highs, breakUp, edge,
                                   dimension, float3(0.52, 0.19, 0.035), float3(1.0, 0.94, 0.37), 1);
    float4 right = MagnetChoirField(shardPoint, anchor, motionTime, spacing, highs, breakUp, edge,
                                    dimension, float3(0.18, 0.08, 0.54), float3(0.28, 0.88, 1.0), -1);
    return right + left * (1 - right.a);
}
