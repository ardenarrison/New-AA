#version 450

// Orbit lines for DLAA, drawn after DLSS so they never go through temporal accumulation.
// Each instance is one segment of a line strip: it reads point i and point i+1 (the same vertex buffer bound twice, one element apart)
// and expands them into a screen-space quad, so the edges can be antialiased in the fragment shader.
// Positions are in ego space, like the game's own orbit lines. The view-projection here is the unjittered one.
layout(location = 0) in vec3 inP0;
layout(location = 1) in vec3 inP1;
layout(location = 2) in uvec4 inC0;
layout(location = 3) in uvec4 inC1;

layout(location = 0) out vec4 outColor;
layout(location = 1) out float outSide;

layout(push_constant) uniform LinePush
{
    mat4 viewProjection;
    vec4 params; // x: quad half width in pixels (line plus fringe), y: render width, z: render height, w: near clip w
} pc;

const float colourDivision = 1.0 / 255.0;
const float Epsilon = 1e-10;

// Orbit line colouring. Like the game's orbit lines, the colour's saturation and lightness are scaled by its alpha (never below 0.1),
// which darkens faint lines instead of letting them wash out.
vec3 rgbToHsl(vec3 c)
{
    float hi = max(c.r, max(c.g, c.b));
    float lo = min(c.r, min(c.g, c.b));
    float chroma = hi - lo;
    float l = 0.5 * (hi + lo);
    float s = chroma / (1.0 - abs(2.0 * l - 1.0) + Epsilon);
    float h = 0.0;
    if (chroma > 0.0)
    {
        if (hi == c.r)
        {
            h = mod((c.g - c.b) / chroma, 6.0);
        }
        else if (hi == c.g)
        {
            h = (c.b - c.r) / chroma + 2.0;
        }
        else
        {
            h = (c.r - c.g) / chroma + 4.0;
        }
        h /= 6.0;
    }
    return vec3(h, s, l);
}

vec3 hslToRgb(float h, float s, float l)
{
    float chroma = (1.0 - abs(2.0 * l - 1.0)) * s;
    vec3 k = clamp(abs(mod(h * 6.0 + vec3(0.0, 4.0, 2.0), 6.0) - 3.0) - 1.0, 0.0, 1.0);
    return (l - 0.5 * chroma) + chroma * k;
}

vec4 lineColor(uvec4 c)
{
    vec3 rgb = vec3(c.rgb) * colourDivision;
    float alpha = float(c.a) * colourDivision;
    vec3 hsl = rgbToHsl(rgb);
    float saturation = clamp(hsl.y * alpha, 0.1, 1.0);
    float lightness = clamp(hsl.z * alpha, 0.1, 1.0);
    return vec4(hslToRgb(hsl.x, saturation, lightness), alpha);
}

void main()
{
    bool atEnd = (gl_VertexIndex & 2) != 0;
    float side = ((gl_VertexIndex & 1) != 0) ? 1.0 : -1.0;

    // The game ends a strip with a point at float.MaxValue.
    bool valid = abs(inP0.x) < 1e30 && abs(inP1.x) < 1e30;

    vec4 c0 = pc.viewProjection * vec4(valid ? inP0 : vec3(0.0), 1.0);
    vec4 c1 = pc.viewProjection * vec4(valid ? inP1 : vec3(1.0), 1.0);

    // Clip the segment, in clip space, to the near plane and to a band around the screen. Orbit lines are enormous and often pass behind
    // the camera. Cut at the near plane alone, an end can land billions of pixels off screen, where a float cannot hold a one pixel
    // width: the quad collapses and the segment vanishes or bends. Inside the band every screen coordinate is a few thousand pixels,
    // which a float holds to a small fraction of a pixel.
    // Each plane is "distance >= 0 is inside". The segment is cut by walking t along c0 -> c1 (clip space is linear in t).
    const float guardBand = 4.0;
    float nearW = pc.params.w;
    float d0[5] = float[5](c0.w - nearW, guardBand * c0.w - c0.x, guardBand * c0.w + c0.x, guardBand * c0.w - c0.y, guardBand * c0.w + c0.y);
    float d1[5] = float[5](c1.w - nearW, guardBand * c1.w - c1.x, guardBand * c1.w + c1.x, guardBand * c1.w - c1.y, guardBand * c1.w + c1.y);
    float t0 = 0.0;
    float t1 = 1.0;
    for (int i = 0; i < 5; i++)
    {
        if (d0[i] < 0.0 && d1[i] < 0.0)
        {
            valid = false;
        }
        else if (d0[i] < 0.0)
        {
            t0 = max(t0, d0[i] / (d0[i] - d1[i]));
        }
        else if (d1[i] < 0.0)
        {
            t1 = min(t1, d0[i] / (d0[i] - d1[i]));
        }
    }
    if (t0 >= t1)
    {
        valid = false;
    }
    vec4 a = mix(c0, c1, t0);
    vec4 b = mix(c0, c1, t1);
    vec4 colorA = mix(lineColor(inC0), lineColor(inC1), t0);
    vec4 colorB = mix(lineColor(inC0), lineColor(inC1), t1);

    vec2 halfSize = pc.params.yz * 0.5;
    vec2 s0 = a.xy / max(a.w, 1e-6) * halfSize;
    vec2 s1 = b.xy / max(b.w, 1e-6) * halfSize;
    vec2 dir = s1 - s0;
    float len = length(dir);
    if (len < 1e-4)
    {
        valid = false;
    }
    dir /= max(len, 1e-4);
    vec2 normal = vec2(-dir.y, dir.x);

    float halfWidth = pc.params.x;
    float w = atEnd ? b.w : a.w;
    float z = atEnd ? b.z : a.z;
    // The quad also extends half a width past each end, so joints between segments have no gaps.
    vec2 s = (atEnd ? s1 + dir * halfWidth : s0 - dir * halfWidth) + normal * side * halfWidth;

    gl_Position = valid ? vec4(s / halfSize * w, max(z, 0.0), w) : vec4(0.0, 0.0, 2.0, 1.0);
    outColor = atEnd ? colorB : colorA;
    outSide = side * halfWidth;
}
