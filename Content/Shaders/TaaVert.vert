#version 450

// Fullscreen quad for the TAA passes. Vertex order is counter-clockwise on screen (the game culls clockwise).
layout(location = 0) out vec2 vTexcoord;

void main()
{
    vec2 uv = vec2(float((gl_VertexIndex >> 1) & 1), float(gl_VertexIndex & 1));
    vTexcoord = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
