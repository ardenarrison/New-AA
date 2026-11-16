#version 450

// Copies the TAA result into the image the final pass shows, with light sharpening. The history keeps the unsharpened result, so the
// sharpening never builds up from frame to frame.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uColor; // the resolved TAA history

layout(push_constant) uniform SharpenPush
{
    vec4 rtMetrics; // x = 1/width, y = 1/height, z = width, w = height
} pc;

// 0 = off. The resolve's reconstruction filter and the history resampling soften the image slightly. This brings most of that back.
const float SHARPNESS = 0.3;

void main()
{
    ivec2 size = ivec2(pc.rtMetrics.zw);
    ivec2 p = ivec2(gl_FragCoord.xy);
    vec3 c = texelFetch(uColor, p, 0).rgb;
    vec3 n = texelFetch(uColor, clamp(p + ivec2(0, -1), ivec2(0), size - 1), 0).rgb;
    vec3 s = texelFetch(uColor, clamp(p + ivec2(0, 1), ivec2(0), size - 1), 0).rgb;
    vec3 w = texelFetch(uColor, clamp(p + ivec2(-1, 0), ivec2(0), size - 1), 0).rgb;
    vec3 e = texelFetch(uColor, clamp(p + ivec2(1, 0), ivec2(0), size - 1), 0).rgb;

    // Unsharp mask against the four neighbours, kept inside their range so it cannot overshoot or ring.
    vec3 sharpened = c + (c - 0.25 * (n + s + w + e)) * SHARPNESS;
    vec3 lo = min(c, min(min(n, s), min(w, e)));
    vec3 hi = max(c, max(max(n, s), max(w, e)));
    outColor = vec4(clamp(sharpened, lo, hi), 1.0);
}
