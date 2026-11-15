#version 450

// Final image for DLAA with orbit lines: the DLSS output (or the LDR scene when DLSS is unavailable) with the tonemapped lines on top.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uBase;
layout(set = 0, binding = 1) uniform sampler2D uLines;    // lines, tonemapped like the scene
layout(set = 0, binding = 2) uniform sampler2D uCoverage; // alpha channel: line coverage

void main()
{
    vec3 base = texture(uBase, vTexcoord).rgb;
    vec3 lines = texture(uLines, vTexcoord).rgb;
    float alpha = texture(uCoverage, vTexcoord).a;
    outColor = vec4(mix(base, lines, alpha), 1.0);
}
