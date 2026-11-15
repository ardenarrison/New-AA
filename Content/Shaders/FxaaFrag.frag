#version 450

// FXAA 3.11 (quality, 12-step edge search).
// Adapted from Timothy Lottes' public-domain FXAA reference implementation.
// Input: the tone-mapped, display-referred LDR scene (R8G8B8A8 UNorm).

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSceneLdr;

layout(push_constant) uniform FxaaPush
{
    vec4 rtMetrics; // x = 1/width, y = 1/height, z = width, w = height
} pc;

const float EDGE_THRESHOLD_MIN = 0.0312;
const float EDGE_THRESHOLD     = 0.125;
const float SUBPIX_QUALITY     = 0.75;
const int   SEARCH_STEPS       = 12;
const float SEARCH_STEP[12]    = float[12](1.0, 1.0, 1.0, 1.0, 1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0);

float luma(vec3 c)
{
    return dot(c, vec3(0.299, 0.587, 0.114));
}

float lumaAt(vec2 uv)
{
    return luma(texture(uSceneLdr, uv).rgb);
}

void main()
{
    vec2 rcp  = pc.rtMetrics.xy;
    vec2 posM = inUv;

    float lumaM = lumaAt(posM);
    float lumaN = lumaAt(posM + vec2(0.0, -rcp.y));
    float lumaS = lumaAt(posM + vec2(0.0,  rcp.y));
    float lumaW = lumaAt(posM + vec2(-rcp.x, 0.0));
    float lumaE = lumaAt(posM + vec2( rcp.x, 0.0));

    float rangeMin = min(lumaM, min(min(lumaN, lumaW), min(lumaS, lumaE)));
    float rangeMax = max(lumaM, max(max(lumaN, lumaW), max(lumaS, lumaE)));
    float range    = rangeMax - rangeMin;

    // Not an edge: pass the pixel through untouched.
    if (range < max(EDGE_THRESHOLD_MIN, rangeMax * EDGE_THRESHOLD))
    {
        outColor = vec4(texture(uSceneLdr, posM).rgb, 1.0);
        return;
    }

    float lumaNW = lumaAt(posM + vec2(-rcp.x, -rcp.y));
    float lumaNE = lumaAt(posM + vec2( rcp.x, -rcp.y));
    float lumaSW = lumaAt(posM + vec2(-rcp.x,  rcp.y));
    float lumaSE = lumaAt(posM + vec2( rcp.x,  rcp.y));

    float lumaNS   = lumaN + lumaS;
    float lumaWE   = lumaW + lumaE;
    float lumaNWSW = lumaNW + lumaSW;
    float lumaNESE = lumaNE + lumaSE;
    float lumaNWNE = lumaNW + lumaNE;
    float lumaSWSE = lumaSW + lumaSE;

    // Decide whether the edge is mostly horizontal or vertical.
    float edgeHorz1 = -2.0 * lumaM + lumaNS;
    float edgeVert1 = -2.0 * lumaM + lumaWE;
    float edgeHorz2 = -2.0 * lumaE + lumaNESE;
    float edgeVert2 = -2.0 * lumaN + lumaNWNE;
    float edgeHorz3 = -2.0 * lumaW + lumaNWSW;
    float edgeVert3 = -2.0 * lumaS + lumaSWSE;
    float edgeHorz4 = abs(edgeHorz1) * 2.0 + abs(edgeHorz2);
    float edgeVert4 = abs(edgeVert1) * 2.0 + abs(edgeVert2);
    float edgeHorz  = abs(edgeHorz3) + edgeHorz4;
    float edgeVert  = abs(edgeVert3) + edgeVert4;

    bool horzSpan = edgeHorz >= edgeVert;

    // Pick the luma pair across the edge and the gradient direction.
    float lengthSign = horzSpan ? rcp.y : rcp.x;
    float luma1      = horzSpan ? lumaN : lumaW;
    float luma2      = horzSpan ? lumaS : lumaE;
    float gradient1  = luma1 - lumaM;
    float gradient2  = luma2 - lumaM;
    bool  steepest1  = abs(gradient1) >= abs(gradient2);
    float gradientScaled = 0.25 * max(abs(gradient1), abs(gradient2));

    float lumaLocalAverage;
    if (steepest1)
    {
        lengthSign = -lengthSign;
        lumaLocalAverage = 0.5 * (luma1 + lumaM);
    }
    else
    {
        lumaLocalAverage = 0.5 * (luma2 + lumaM);
    }

    // Start point sits half a pixel off-centre, on the edge.
    vec2 posB = posM;
    vec2 offNP;
    if (horzSpan)
    {
        posB.y += lengthSign * 0.5;
        offNP = vec2(rcp.x, 0.0);
    }
    else
    {
        posB.x += lengthSign * 0.5;
        offNP = vec2(0.0, rcp.y);
    }

    // Sub-pixel aliasing estimate.
    float lumaAvg = (2.0 * (lumaNS + lumaWE) + lumaNWSW + lumaNESE) * (1.0 / 12.0);
    float subpixC = clamp(abs(lumaAvg - lumaM) / range, 0.0, 1.0);
    float subpixF = (-2.0 * subpixC + 3.0) * subpixC * subpixC;
    float pixelOffsetSubpix = subpixF * subpixF * SUBPIX_QUALITY;

    // Walk along the edge in both directions until the luma leaves the local average.
    vec2  posN = posB;
    vec2  posP = posB;
    float lumaEndN = 0.0;
    float lumaEndP = 0.0;
    bool  doneN = false;
    bool  doneP = false;
    for (int i = 0; i < SEARCH_STEPS; ++i)
    {
        float stepSize = SEARCH_STEP[i];
        if (!doneN)
        {
            posN -= offNP * stepSize;
            lumaEndN = lumaAt(posN) - lumaLocalAverage;
            doneN = abs(lumaEndN) >= gradientScaled;
        }
        if (!doneP)
        {
            posP += offNP * stepSize;
            lumaEndP = lumaAt(posP) - lumaLocalAverage;
            doneP = abs(lumaEndP) >= gradientScaled;
        }
        if (doneN && doneP)
        {
            break;
        }
    }

    // Distance from the pixel centre to the nearer end of the edge span.
    float distN = horzSpan ? (posM.x - posN.x) : (posM.y - posN.y);
    float distP = horzSpan ? (posP.x - posM.x) : (posP.y - posM.y);
    bool  directionN = distN < distP;
    float dist       = directionN ? distN : distP;
    float spanLength = max(distN + distP, 1e-6);

    float lumaEnd = directionN ? lumaEndN : lumaEndP;
    bool  lumaMBelowAvg = (lumaM - lumaLocalAverage) < 0.0;
    bool  goodSpan = (lumaEnd < 0.0) != lumaMBelowAvg;

    float pixelOffset      = goodSpan ? (-dist / spanLength + 0.5) : 0.0;
    float pixelOffsetFinal = max(pixelOffset, pixelOffsetSubpix);

    if (horzSpan)
    {
        posM.y += pixelOffsetFinal * lengthSign;
    }
    else
    {
        posM.x += pixelOffsetFinal * lengthSign;
    }

    outColor = vec4(texture(uSceneLdr, posM).rgb, 1.0);
}
