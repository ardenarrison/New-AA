#version 450

// TAA resolve. Runs in the same pipeline as DLAA and FSR Native AA, so it gets the same jittered camera, camera and per-object
// motion vectors and history resets. For each pixel:
//  1. Rebuild this frame's colour at the pixel centre from the 3x3 neighbourhood. Every sample was rendered at its own pixel centre
//     shifted by this frame's jitter, so each one is weighted by how far that point is from this pixel's centre.
//  2. Take the motion vector of the closest surface in the 3x3 neighbourhood, so the edges of a moving object move with it.
//  3. Read last frame's result where this point was, with a Catmull-Rom filter (bilinear would blur the history a little more every frame).
//  4. Clip that history to the colours around this pixel in this frame (YCoCg, mean +- standard deviation, inside min/max). History
//     that no longer belongs here (something moved away, the lighting changed, a wrong motion vector) is pulled back to what is here now.
//     Stars are exempt: dim stars are smaller than a pixel, so they fall between the samples in some frames of the jitter cycle, and
//     clipping would erase them in those frames. A pixel counts as a star when it is still, has only sky around it this frame and last
//     (nothing that writes depth was near, so no object can have moved away from it), and its history differs from this frame only in
//     a spot a couple of pixels wide (an engine plume or smoke, which write no depth either, leaves a larger area when it moves).
//  5. Blend: a plain average over the first frames after a reset, then keep 90% of the history each frame. Fast motion keeps less,
//     because every reprojection softens the history a little.
// The alpha channel of the history counts the frames averaged so far.
layout(location = 0) in vec2 vTexcoord;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uColor;     // this frame's scene, tonemapped LDR, rendered with this frame's jitter
layout(set = 0, binding = 1) uniform sampler2D uHistory;   // last frame's result: rgb, a = frames averaged
layout(set = 0, binding = 2) uniform sampler2D uMotion;    // offset from this pixel to where it was last frame, in UV units
layout(set = 0, binding = 3) uniform sampler2D uDepth;     // reverse-Z depth (bigger is closer)
layout(set = 0, binding = 4) uniform sampler2D uPrevDepth; // last frame's nearest depth around each pixel (r), kept by TaaDepthFrag

layout(push_constant) uniform TaaPush
{
    vec4 rtMetrics; // x = 1/width, y = 1/height, z = width, w = height
    vec4 params;    // xy = this frame's jitter in pixels, z = 1 to use the history or 0 to start over, w = unused
} pc;

// Weight of this frame once the history has settled. 0.1 averages roughly the last ten frames, a bit more than one 8-frame jitter cycle.
const float SETTLED_WEIGHT = 0.1;

// Weight of this frame when the scene moves FAST_MOTION_PIXELS or more per frame.
const float MOVING_WEIGHT = 0.25;

const float FAST_MOTION_PIXELS = 24.0;

// The frame count stops here. Only 1 / (count + 1) above SETTLED_WEIGHT matters.
const float MAX_FRAMES = 16.0;

// Half-width of the clipping box in standard deviations. Smaller rejects stale history sooner but lets more aliasing through.
const float CLIP_SIGMA = 1.25;

// Falloff of the filter that rebuilds this frame's colour at the pixel centre: exp(-k * d * d), d in pixels. 2.29 approximates a
// Blackman-Harris window one pixel wide.
const float FILTER_FALLOFF = 2.29;

// How much of the unclipped history a star pixel keeps. Everything that passes the star test is a still spot in empty sky, so all of it.
const float STAR_KEEP = 1.0;

// Motion, in pixels per frame, from which a sky pixel counts as moving and its history is fully clipped.
const float STILL_MOTION_PIXELS = 1.0;

// How far (YCoCg units) the history 2 pixels away may lie outside this frame's colour range for a spot to count as a star.
const float STAR_TOLERANCE = 0.02;

vec3 toYCoCg(vec3 c)
{
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
                0.5 * c.r - 0.5 * c.b,
                -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

vec3 toRgb(vec3 c)
{
    float t = c.x - c.z;
    return vec3(t + c.y, c.x + c.z, t - c.y);
}

// Moves c toward the centre of the box until it is inside. Keeps the history's hue better than clamping each channel.
vec3 clipToBox(vec3 c, vec3 lo, vec3 hi)
{
    vec3 center = 0.5 * (lo + hi);
    vec3 extent = max(0.5 * (hi - lo), vec3(1e-4));
    vec3 offset = c - center;
    vec3 units = abs(offset) / extent;
    float m = max(units.x, max(units.y, units.z));
    return m > 1.0 ? center + offset / m : c;
}

// Catmull-Rom filtered history from five bilinear reads (the four corner taps are left out, they carry almost no weight).
vec3 sampleHistory(vec2 uv)
{
    vec2 texel = pc.rtMetrics.xy;
    vec2 position = uv * pc.rtMetrics.zw;
    vec2 center = floor(position - 0.5) + 0.5;
    vec2 f = position - center;
    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;
    vec2 t0 = (center - 1.0) * texel;
    vec2 t3 = (center + 2.0) * texel;
    vec2 t12 = (center + w2 / w12) * texel;

    float wTop = w12.x * w0.y;
    float wLeft = w0.x * w12.y;
    float wMiddle = w12.x * w12.y;
    float wRight = w3.x * w12.y;
    float wBottom = w12.x * w3.y;
    vec3 sum = texture(uHistory, vec2(t12.x, t0.y)).rgb * wTop
             + texture(uHistory, vec2(t0.x, t12.y)).rgb * wLeft
             + texture(uHistory, t12).rgb * wMiddle
             + texture(uHistory, vec2(t3.x, t12.y)).rgb * wRight
             + texture(uHistory, vec2(t12.x, t3.y)).rgb * wBottom;
    return max(sum / (wTop + wLeft + wMiddle + wRight + wBottom), vec3(0.0));
}

// True when the history 2 pixels left, right, above and below lies inside [lo, hi], this frame's colour range around the pixel:
// whatever the history holds here that this frame does not is at most a few pixels wide.
bool isSmallSpot(ivec2 center, ivec2 size, vec3 lo, vec3 hi)
{
    const ivec2 offsets[4] = ivec2[4](ivec2(2, 0), ivec2(-2, 0), ivec2(0, 2), ivec2(0, -2));
    for (int i = 0; i < 4; i++)
    {
        vec3 c = toYCoCg(texelFetch(uHistory, clamp(center + offsets[i], ivec2(0), size - 1), 0).rgb);
        if (any(lessThan(c, lo)) || any(greaterThan(c, hi)))
        {
            return false;
        }
    }
    return true;
}

void main()
{
    ivec2 size = ivec2(pc.rtMetrics.zw);
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec2 jitter = pc.params.xy;

    vec3 current = vec3(0.0);
    float currentWeight = 0.0;
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);
    vec3 lo = vec3(1e9);
    vec3 hi = vec3(-1e9);
    float closestDepth = texelFetch(uDepth, pixel, 0).r;
    ivec2 closestPixel = pixel;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 p = clamp(pixel + ivec2(x, y), ivec2(0), size - 1);
            vec3 c = toYCoCg(texelFetch(uColor, p, 0).rgb);
            // The jitter moved the image by +jitter pixels, so this sample shows the point that belongs at (its centre - jitter).
            vec2 d = vec2(float(x), float(y)) - jitter;
            float w = exp(-FILTER_FALLOFF * dot(d, d));
            current += c * w;
            currentWeight += w;
            m1 += c;
            m2 += c * c;
            lo = min(lo, c);
            hi = max(hi, c);
            float depth = texelFetch(uDepth, p, 0).r;
            if (depth > closestDepth)
            {
                closestDepth = depth;
                closestPixel = p;
            }
        }
    }
    current /= currentWeight;

    m1 /= 9.0;
    vec3 sigma = sqrt(max(m2 / 9.0 - m1 * m1, vec3(0.0)));
    // The variance box inside the min/max box, grown to include this frame's own colour so history that agrees with it is kept.
    vec3 clipLo = min(max(m1 - CLIP_SIGMA * sigma, lo), current);
    vec3 clipHi = max(min(m1 + CLIP_SIGMA * sigma, hi), current);

    vec2 motion = texelFetch(uMotion, closestPixel, 0).rg;
    vec2 uv = (vec2(pixel) + 0.5) * pc.rtMetrics.xy;
    vec2 historyUv = uv + motion;
    bool hasHistory = pc.params.z > 0.5
        && all(greaterThanEqual(historyUv, vec2(0.0)))
        && all(lessThanEqual(historyUv, vec2(1.0)));

    vec3 resolved = current;
    float frames = 0.0;
    if (hasHistory)
    {
        float motionPixels = length(motion * pc.rtMetrics.zw);
        vec3 rawHistory = toYCoCg(sampleHistory(historyUv));
        vec3 history = clipToBox(rawHistory, clipLo, clipHi);
        // Stars: closestDepth is the nearest depth around this pixel now, uPrevDepth the same around where it was last frame.
        // 0 is the far plane, so both are 0 only with nothing but sky around.
        ivec2 historyPixel = clamp(ivec2(historyUv * pc.rtMetrics.zw), ivec2(0), size - 1);
        float stillness = 1.0 - clamp(motionPixels / STILL_MOTION_PIXELS, 0.0, 1.0);
        if (stillness > 0.0 && closestDepth <= 0.0 && texelFetch(uPrevDepth, historyPixel, 0).r <= 0.0
            && isSmallSpot(historyPixel, size, clipLo - STAR_TOLERANCE, clipHi + STAR_TOLERANCE))
        {
            history = mix(history, rawHistory, STAR_KEEP * stillness);
        }
        frames = texture(uHistory, historyUv).a;
        float weight = max(1.0 / (frames + 1.0), SETTLED_WEIGHT);
        weight = max(weight, mix(SETTLED_WEIGHT, MOVING_WEIGHT, clamp(motionPixels / FAST_MOTION_PIXELS, 0.0, 1.0)));
        resolved = mix(history, current, weight);
    }
    outColor = vec4(max(toRgb(resolved), vec3(0.0)), min(frames + 1.0, MAX_FRAMES));
}
