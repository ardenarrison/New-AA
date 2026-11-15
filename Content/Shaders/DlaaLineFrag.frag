#version 450

// Antialiased coverage for the orbit line quads. Writes the straight colour and the coverage-weighted alpha.
// Blending (see DlaaLines.cs): colour replaces, alpha takes the maximum, so overlapping quads at a joint do not darken.
layout(location = 0) in vec4 inColor;
layout(location = 1) in float inSide;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform LinePush
{
    mat4 viewProjection;
    vec4 params;
} pc;

void main()
{
    float coverage = clamp(pc.params.x - abs(inSide), 0.0, 1.0);
    float alpha = inColor.a * coverage;
    if (alpha < 0.004)
    {
        discard;
    }
    outColor = vec4(inColor.rgb, alpha);
}
