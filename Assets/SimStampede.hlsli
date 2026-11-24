// Repeat the same lattice at both canvas edges. Accent motion is derived from
// the envelope age so a kick creates a traveling disturbance, not a flash.
float StampedeBox(float2 p, float2 halfSize, float corner)
{
    float2 q = abs(p) - halfSize + corner;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - corner;
}

float4 SimStampede(int2 pixel, float2 uv, float motionTime, float kick, float snare, float highs, float slam, float held)
{
    float t = motionTime;
    float tau = 6.2831853;
    float planeX = frac(uv.x - t * 0.18);
    float age = 1.0 - saturate(kick);
    float dist = abs(frac(planeX - age * 0.95 + 0.5) - 0.5);
    float wave = pow(saturate(1.0 - dist * 4.8), 1.4) * kick;
    float beatSlam = saturate(slam);
    float2 dimensions = float2(Width, Height);
    float columns = clamp(round(6.0 * dimensions.x / dimensions.y), 4.0, 20.0);
    float aa = max(columns / dimensions.x, 4.0 / dimensions.y) * 0.9;
    float bandY = uv.y - 0.5 + 0.085 * sin(tau * planeX + t * 0.74);
    float strongest = 0, contour = 0, selection = 0;
    float4 sceneSample = S(pixel);

    // Three large overlapping lanes; no unbounded particle spawning or
    // per-pixel source-fetch loop. Wrap is analytic, including the spill.
    [unroll]
    for (int i = 0; i < 3; i++)
    {
        float lane = float(i) - 1.0;
        float spill = snare * (0.36 + 0.25 * lane) * sin(2.0 * tau * planeX + lane * 1.7);
        float trainX = columns * planeX + lane * 0.43 + wave * (0.67 + lane * 0.19) + spill;
        float trainY = (bandY + lane * (0.125 + 0.073 * beatSlam)
                      + snare * lane * 0.073 * cos(tau * planeX + lane)) * 6.2;
        float edge = StampedeBox(float2(frac(trainX) - 0.5, trainY),
                                 float2(0.30 + 0.13 * wave, 0.25 + 0.06 * beatSlam), 0.12);
        float shape = 1.0 - smoothstep(-aa, aa, edge);
        if (shape > strongest) selection = float(i) * 0.5;
        strongest = max(strongest, shape);
        contour = max(contour, shape * smoothstep(-0.105 - 0.02 * highs, -0.035, edge));
    }
    float3 body = lerp(float3(0.86, 0.33, 0.07), float3(0.16, 0.67, 0.98), selection);
    float3 straight = sceneSample.rgb / max(sceneSample.a, 1.0 / 255.0);
    body = lerp(body, straight, sceneSample.a * 0.23);
    float3 trim = lerp(float3(0.65, 0.89, 1.0), float3(0.82, 0.85, 0.95), beatSlam);
    float alpha = saturate(strongest);
    return float4(saturate(lerp(body, trim, contour * (0.50 + wave * 0.34))) * alpha, alpha);
}
