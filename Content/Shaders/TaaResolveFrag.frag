#version 450

// TAA resolve. Reprojects the history with the depth buffer and the previous frame's view-projection,
// clamps it to the current 3x3 neighborhood in YCoCg, then blends it with the current frame.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uColor;    // current tonemapped scene (LDR)
layout(set = 0, binding = 1) uniform sampler2D uHistory;  // previous resolved frame
layout(set = 0, binding = 2) uniform sampler2D uDepth;    // reverse-Z depth of the current frame

layout(push_constant) uniform TaaPush
{
    mat4 prevViewProjection;
    mat4 invViewProjection;
    vec4 rtMetrics; // x = 1/width, y = 1/height, z = width, w = height
    float historyWeight;
    float cameraMotion; // 0 when the camera is still
} pc;

// History weight while the camera is still (high: average many frames) and while it moves (low: trust the current frame,
// because reprojection is less accurate while moving and history would smear thin lines).
const float STILL_HISTORY = 0.95;
const float MOVING_HISTORY = 0.25;

vec3 toYCoCg(vec3 c)
{
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
                0.5 * c.r - 0.5 * c.b,
                -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

vec3 toRgb(vec3 y)
{
    float t = y.x - y.z;
    return vec3(t + y.y, y.x + y.z, t - y.y);
}

void main()
{
    vec2 uv = vTexcoord;
    vec2 texel = pc.rtMetrics.xy;

    // When the camera is still, the history is at the same pixel. Skip reprojection so that
    // it cannot introduce error, and the jitter averages over frames.
    bool still = pc.cameraMotion < 1e-5;
    vec2 prevUv = uv;
    bool valid = true;
    if (!still)
    {
        // Pixel -> world, using the unjittered inverse view-projection.
        float depth = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
        vec4 wp = pc.invViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
        float w = (abs(wp.w) < 1e-8) ? 1e-8 : wp.w;
        vec3 world = wp.xyz / w;

        // World -> previous screen position.
        vec4 prevClip = pc.prevViewProjection * vec4(world, 1.0);
        prevUv = (prevClip.xy / max(abs(prevClip.w), 1e-8)) * 0.5 + 0.5;
        valid = prevClip.w > 0.0
             && all(greaterThanEqual(prevUv, vec2(0.0)))
             && all(lessThanEqual(prevUv, vec2(1.0)));
    }

    // Neighborhood statistics in YCoCg over the 3x3 block: min/max, mean and standard deviation.
    vec3 curY = toYCoCg(texture(uColor, uv).rgb);
    vec3 cur = texture(uColor, uv).rgb;
    vec3 minY = curY;
    vec3 maxY = curY;
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            vec3 s = toYCoCg(texture(uColor, uv + vec2(float(x), float(y)) * texel).rgb);
            minY = min(minY, s);
            maxY = max(maxY, s);
            m1 += s;
            m2 += s * s;
        }
    }
    m1 /= 9.0;
    m2 /= 9.0;
    vec3 sigma = sqrt(max(m2 - m1 * m1, vec3(0.0)));

    // Variance box, intersected with the min/max box. Tighter than min/max alone, so stale history is rejected sooner.
    const float GAMMA = 1.0;
    vec3 boxMin = max(m1 - GAMMA * sigma, minY);
    vec3 boxMax = min(m1 + GAMMA * sigma, maxY);

    vec3 result = cur;
    if (valid && pc.historyWeight > 0.0)
    {
        vec3 hist = texture(uHistory, prevUv).rgb;
        vec3 histY = toYCoCg(hist);
        vec3 clampedY = clamp(histY, boxMin, boxMax);

        // Confidence in the history. Low when the history had to be pulled far into the box, which means it disagrees with the scene.
        float clampDistance = length(histY - clampedY);
        float confidence = 1.0 - clamp(clampDistance * 2.0, 0.0, 1.0);

        // Low when reprojection moves the pixel more than a fraction of a pixel, which means the camera is moving.
        float velocityPx = length((prevUv - uv) * pc.rtMetrics.zw);
        confidence *= 1.0 - smoothstep(0.5, 2.0, velocityPx);

        // Blend between the still and moving history weights, based on how much the camera moved this frame.
        float stillness = 1.0 - smoothstep(1e-5, 1e-3, pc.cameraMotion);
        float baseWeight = mix(MOVING_HISTORY, STILL_HISTORY, stillness);
        float weight = baseWeight * confidence;
        result = mix(cur, toRgb(clampedY), weight);
    }
    outColor = vec4(result, 1.0);
}
