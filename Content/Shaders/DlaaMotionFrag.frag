#version 450

// Camera motion vectors for DLAA. Output is the offset from this pixel to where it was last frame, in UV units.
// The game's view matrix holds only the camera's rotation (positions are stored relative to the camera), so the camera's
// translation relative to the followed object comes in separately and is added to the reconstructed position.
// Moving objects are not included yet, because the engine does not write per-object motion.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec2 outMotion;

layout(set = 0, binding = 0) uniform sampler2D uDepth;

layout(push_constant) uniform MotionPush
{
    mat4 prevViewProjection;
    mat4 invViewProjection;
    vec4 rtMetrics;
    vec4 cameraTranslation; // xyz: camera movement since last frame, w: total camera motion (0 when still)
} pc;

void main()
{
    if (pc.cameraTranslation.w < 1e-5)
    {
        outMotion = vec2(0.0);
        return;
    }

    vec2 uv = vTexcoord;
    float depth = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
    vec4 wp = pc.invViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    float w = (abs(wp.w) < 1e-8) ? 1e-8 : wp.w;
    vec3 ego = wp.xyz / w;

    // Last frame the camera was at (now - translation), so the same point sat at ego + translation relative to it.
    vec4 prevClip = pc.prevViewProjection * vec4(ego + pc.cameraTranslation.xyz, 1.0);
    vec2 prevUv = (prevClip.xy / max(abs(prevClip.w), 1e-8)) * 0.5 + 0.5;
    outMotion = prevUv - uv;
}
