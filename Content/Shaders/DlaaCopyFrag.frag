#version 450

// Copies the DLAA output (or the LDR scene when DLAA is unavailable) to the final target. No sharpening: DLAA is already sharp.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uColor;

void main()
{
    outColor = vec4(texture(uColor, vTexcoord).rgb, 1.0);
}
