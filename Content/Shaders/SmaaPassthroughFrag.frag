#version 450

// Diagnostic: copies the LDR scene to the final target. Used with SmaaDebug.PassthroughBlend.
layout(location = 0) in vec2 vTexcoord;
layout(location = 1) in vec4 vOffset;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uColor;
layout(set = 0, binding = 1) uniform sampler2D uBlend;

void main()
{
    outColor = vec4(texture(uColor, vTexcoord).rgb, 1.0);
}
