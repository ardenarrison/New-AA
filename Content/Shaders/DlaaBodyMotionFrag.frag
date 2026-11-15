#version 450

// Motion vectors for one rigid body (a vehicle or a celestial body), drawn over the camera-only vectors.
// Pixels whose reconstructed position lies inside the body's volume get the body's own motion. Every other pixel is discarded,
// so the underlying value stays. Bodies are drawn big to small, so a vehicle on a planet overwrites the planet's value.
//
// For a point p (ego space, this frame) fixed to the body, last frame it was at  p + B * p + T  in last frame's ego space:
//   B = prevOrientation * inverse(currentOrientation) - identity     (kept small so float precision survives)
//   T = prevCentre - prevOrientation * inverse(currentOrientation) * currentCentre   (computed in double on the CPU)
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec2 outMotion;

layout(set = 0, binding = 0) uniform sampler2D uDepth;

layout(push_constant) uniform BodyPush
{
    mat4 prevViewProjection;
    mat4 invViewProjection;
    vec4 center;      // xyz: body centre in ego space this frame, w: outer radius
    vec4 info;        // x: inner radius (nothing closer to the centre belongs to the body)
    vec4 bCol0;       // columns of B
    vec4 bCol1;
    vec4 bCol2;
    vec4 translation; // xyz: T
} pc;

void main()
{
    float depth = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
    if (depth <= 0.0)
    {
        discard; // sky: reverse-Z far plane
    }

    vec4 wp = pc.invViewProjection * vec4(vTexcoord * 2.0 - 1.0, depth, 1.0);
    float w = (abs(wp.w) < 1e-8) ? 1e-8 : wp.w;
    vec3 p = wp.xyz / w;

    vec3 d = p - pc.center.xyz;
    float r2 = dot(d, d);
    float outer = pc.center.w;
    float inner = pc.info.x;
    if (r2 > outer * outer || r2 < inner * inner)
    {
        discard;
    }

    vec3 prevP = p + p.x * pc.bCol0.xyz + p.y * pc.bCol1.xyz + p.z * pc.bCol2.xyz + pc.translation.xyz;
    vec4 prevClip = pc.prevViewProjection * vec4(prevP, 1.0);
    vec2 prevUv = (prevClip.xy / max(abs(prevClip.w), 1e-8)) * 0.5 + 0.5;
    outMotion = prevUv - vTexcoord;
}
