// Liquid glass rim for the dock's specular layer: a signed-distance lens that bends the layer's sheen toward the
// rim, chromatic dispersion along the bend, and directional + ambient rim highlights computed from the edge
// normal. Applied only to the specular overlay, never to content, so icons and text stay crisp; the interior of
// the dock early-outs, so the working area is the rim band alone.
//
// The refraction, dispersion and highlight math is adapted from AndroidLiquidGlass,
// Copyright 2025 Kyant, Apache License 2.0 (https://github.com/Kyant0/AndroidLiquidGlass).
// Reimplemented in HLSL ps_3_0 for WPF; compiled with d3dcompiler_47 (see LiquidGlass.ps beside this file).

sampler2D implicitInput : register(s0);

float2 uSize             : register(c0); // layer size, px
float  uRadius           : register(c1); // corner radius, px (0 for the square dock)
float  uRefractionHeight : register(c2); // rim band depth, px
float  uRefractionAmount : register(c3); // maximum displacement, px (negated: bends inward, as the source does)
float  uLightAngle       : register(c4); // radians; the fixed top light
float  uFalloff          : register(c5); // highlight tightness
float  uHighlightAlpha   : register(c6); // directional rim strength, 0..1
float  uAmbientAlpha     : register(c7); // ambient rim strength, 0..1
float  uDispersion       : register(c8); // 1 enables the seven-tap chromatic fringe
float  uRimOnly          : register(c9); // 1 = the interior renders transparent (the wallpaper-refraction layer:
                                         // only the bent rim band shows; the real blur owns the middle)

float sdRoundedRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - (halfSize - float2(r, r));
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

float2 gradSdRoundedRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - (halfSize - float2(r, r));
    if (q.x >= 0.0 || q.y >= 0.0) return sign(p) * normalize(max(q, 0.0001));
    float gx = step(q.y, q.x);
    return sign(p) * float2(gx, 1.0 - gx);
}

// The circular lens profile: zero deep inside the glass, maximal at the rim.
float circleMap(float x) { return 1.0 - sqrt(1.0 - x * x); }

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 halfSize = uSize * 0.5;
    float2 centered = uv * uSize - halfSize;
    float sd = sdRoundedRect(centered, halfSize, uRadius);
    // The effect's output is not clipped by the host Border's corner radius, and a layered window has no DWM
    // corner clip either: everything outside the rounded outline must come from the shader as zero, with one
    // antialiased half-pixel at the boundary.
    float outline = saturate(0.5 - sd);
    float4 color = tex2D(implicitInput, uv);
    if (-sd >= uRefractionHeight) return color * (1.0 - uRimOnly) * outline;

    float sdi = min(sd, 0.0);
    float gradRadius = min(uRadius * 1.5, min(halfSize.x, halfSize.y));
    float2 grad = gradSdRoundedRect(centered, halfSize, gradRadius);
    float d = circleMap(1.0 - (-sdi) / uRefractionHeight) * (-uRefractionAmount);

    float2 refracted = uv + (d * grad) / uSize;
    float dispersionIntensity = uDispersion * ((centered.x * centered.y) / (halfSize.x * halfSize.y));
    float2 dispersed = ((d * grad) * dispersionIntensity) / uSize;

    if (uDispersion > 0.5)
    {
        // Seven taps red through purple, weighted as the source weights them.
        float4 c = float4(0, 0, 0, 0);
        float4 t;
        t = tex2D(implicitInput, refracted + dispersed);                 c.r += t.r / 3.5;                    c.a += t.a / 7.0;
        t = tex2D(implicitInput, refracted + dispersed * (2.0 / 3.0));  c.r += t.r / 3.5; c.g += t.g / 7.0;  c.a += t.a / 7.0;
        t = tex2D(implicitInput, refracted + dispersed * (1.0 / 3.0));  c.r += t.r / 3.5; c.g += t.g / 3.5;  c.a += t.a / 7.0;
        t = tex2D(implicitInput, refracted);                             c.g += t.g / 3.5;                    c.a += t.a / 7.0;
        t = tex2D(implicitInput, refracted - dispersed * (1.0 / 3.0));  c.g += t.g / 3.5; c.b += t.b / 3.0;  c.a += t.a / 7.0;
        t = tex2D(implicitInput, refracted - dispersed * (2.0 / 3.0));  c.b += t.b / 3.0;                    c.a += t.a / 7.0;
        t = tex2D(implicitInput, refracted - dispersed);                c.r += t.r / 7.0; c.b += t.b / 3.0;  c.a += t.a / 7.0;
        color = c;
    }
    else
    {
        color = tex2D(implicitInput, refracted);
    }

    // Rim light: how directly the edge normal faces the light, tightened by the falloff; the directional term
    // lights the facing side only, the ambient term rounds out the rest. Premultiplied additive white.
    // The specular layer owns the highlights; the wallpaper-refraction layer only bends, or they would double.
    float2 lightNormal = float2(cos(uLightAngle), sin(uLightAngle));
    float facing = dot(grad, lightNormal);
    float rim = pow(abs(facing), uFalloff);
    float highlight = saturate(rim * (uHighlightAlpha * step(0.0, facing) + uAmbientAlpha)) * (1.0 - uRimOnly);
    color.rgb += highlight;
    color.a = saturate(color.a + highlight);
    // The wallpaper rim band melts into the interior instead of ending in a one-pixel cliff: full strength at
    // the edge, gone by the band's inner boundary, so it reads as bent glass rather than a painted frame.
    float band = 1.0 - smoothstep(0.55, 1.0, (-sd) / uRefractionHeight);
    float fade = lerp(1.0, band, uRimOnly);
    return color * outline * fade;
}
