#version 450

// Keeps the nearest depth around each pixel (its 3x3 neighbourhood) for the next frame's TAA resolve. The resolve uses it to tell
// pixels that showed only sky last frame from ones where something was close by (and may have moved away since).
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outDepth;

layout(set = 0, binding = 0) uniform sampler2D uDepth; // reverse-Z depth of this frame (bigger is closer, 0 is the far plane)

layout(push_constant) uniform DepthPush
{
    vec4 rtMetrics; // x = 1/width, y = 1/height, z = width, w = height
} pc;

void main()
{
    ivec2 size = ivec2(pc.rtMetrics.zw);
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float nearest = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            nearest = max(nearest, texelFetch(uDepth, clamp(pixel + ivec2(x, y), ivec2(0), size - 1), 0).r);
        }
    }
    outDepth = vec4(nearest, 0.0, 0.0, 1.0);
}
