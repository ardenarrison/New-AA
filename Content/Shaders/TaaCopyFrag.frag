#version 450

// Copies the resolved TAA history to the final target and sharpens it.
// Sharpening is applied here, not in the history buffer, so it does not build up across frames.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uColor;

layout(push_constant) uniform CopyPush
{
    vec4 rtMetrics; // x = 1/width, y = 1/height, z = width, w = height
} pc;

// Sharpening strength. 0 = off. Around 0.3 to 0.5 is a good range.
const float SHARPNESS = 0.4;

void main()
{
    vec2 texel = pc.rtMetrics.xy;
    vec3 c = texture(uColor, vTexcoord).rgb;
    vec3 n = texture(uColor, vTexcoord + vec2(0.0, -texel.y)).rgb;
    vec3 s = texture(uColor, vTexcoord + vec2(0.0,  texel.y)).rgb;
    vec3 w = texture(uColor, vTexcoord + vec2(-texel.x, 0.0)).rgb;
    vec3 e = texture(uColor, vTexcoord + vec2( texel.x, 0.0)).rgb;

    // Unsharp mask against the 4-neighbour average, clamped to the local range so it cannot ring.
    vec3 blur = (n + s + w + e) * 0.25;
    vec3 sharpened = c + (c - blur) * SHARPNESS;
    vec3 minC = min(min(min(n, s), min(w, e)), c);
    vec3 maxC = max(max(max(n, s), max(w, e)), c);
    sharpened = clamp(sharpened, minC, maxC);

    outColor = vec4(sharpened, 1.0);
}
