// Liquid-glass speech bubble for passthrough MR (one quad, signed-distance field).
//
// Shape: a glass orb beside the speaker + a rounded panel that grows out of it, joined by a smooth neck
// (smooth-min union). _Grow 0 → orb only, 1 → full bubble.
//
// Look (after Apple's Liquid Glass, see charlesgrassi.dev/blog/apple-liquid-glass):
//   bevel normals from the SDF gradient (panel) / a dome (orb) → refraction of what is behind the glass,
//   slight chromatic dispersion at the rim, specular highlight + Fresnel rim, light white tint.
// As in Apple's Liquid Glass (WWDC 2025): the highlights come from a light fixed in the room, so they slide along
// the rim as you or the speaker move; and the glass adapts to what is behind it — dimmed over bright backgrounds
// (white captions stay readable against a window) and lifted a little over dark ones (the glass stays visible).
// The "background" is the Quest passthrough camera image (set globally by GlassBackdrop: _PtCamTex,
// _PtWorldToCam, _PtIntrinsics). Without it, the panel falls back to a frosted grey.
//
// Finish, after the web "glass button" (the React component the design uses): a soft diagonal sheen (-75°, white
// 5% → 22% → 5%), a thin rim lit white with two darker turns of a conic gradient, a light inner glow along the bottom
// edge, a faint dark one along the top, and a soft drop shadow under the glass (_Glass: 0 = off, 1 = full).
//
// Alpha is written explicitly (ColorMask RGBA): the Quest blends the app over passthrough by alpha.
Shader "MetaStarter/LiquidGlassBubble"
{
    Properties
    {
        _Body ("Panel size (m) xy, corner radius, bevel width", Vector) = (0.24, 0.08, 0.03, 0.01)
        _BodyCenter ("Panel centre relative to orb (m)", Vector) = (0.15, 0.06, 0, 0)
        _Orb ("Orb radius (m), neck smoothness (m)", Vector) = (0.014, 0.022, 0, 0)
        _Quad ("Quad centre xy, size zw (m, relative to orb)", Vector) = (0.1, 0.03, 0.3, 0.14)
        _Grow ("Grow 0 orb → 1 bubble", Range(0, 1)) = 1
        _Hover ("Pointer glow: centre xy (m, quad space), radius (m), strength", Vector) = (0, 0, 0.02, 0)
        _Tint ("Glass tint", Color) = (1, 1, 1, 0.10)
        _Frost ("Fallback frost (no camera)", Color) = (0.55, 0.57, 0.61, 0.55)
        _Refraction ("Refraction strength (uv)", Float) = 0.035
        _Dispersion ("Chromatic dispersion", Float) = 0.004
        _BlurRadius ("Backdrop blur radius (uv)", Float) = 0.006
        _PtStrength ("Camera backdrop strength", Range(0, 1)) = 0.85
        _Adapt ("Adapt to bright/dark backdrops", Range(0, 1)) = 0.7
        _LightDir ("Room light direction (world, towards the light)", Vector) = (-0.4, 1, 0.3, 0)
        _Alpha ("Overall alpha", Range(0, 1)) = 1
        _Glass ("Glass-button finish", Range(0, 1)) = 1
        _Clear ("Clear glass (Liquid Glass): room shows through, only the rim refracts", Range(0, 1)) = 0
        _ClearDim ("Clear glass: how much it darkens the room behind", Range(0, 1)) = 0.32
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Pass
        {
            Name "LiquidGlass"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ColorMask RGBA
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Body;
                float4 _BodyCenter;
                float4 _Orb;
                float4 _Quad;
                float4 _Hover;
                half _Grow;
                half4 _Tint;
                half4 _Frost;
                float _Refraction;
                float _Dispersion;
                float _BlurRadius;
                half _PtStrength;
                half _Adapt;
                float4 _LightDir;
                half _Alpha;
                half _Glass;
                half _Clear;
                half _ClearDim;
            CBUFFER_END

            // Set by GlassBackdrop (C#) from the passthrough camera.
            TEXTURE2D(_PtCamTex); SAMPLER(sampler_PtCamTex);
            float4x4 _PtWorldToCam;
            float4 _PtIntrinsics;   // fx, fy, cx, cy in viewport units
            float _PtCamEnabled;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;        // metres relative to the orb centre
                float3 positionWS : TEXCOORD1;
                float3 viewWS : TEXCOORD2;
                float3 rightWS : TEXCOORD3;
                float3 upWS : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                ZERO_INITIALIZE(Varyings, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.p = (v.uv - 0.5) * _Quad.zw + _Quad.xy;
                o.viewWS = GetWorldSpaceViewDir(o.positionWS);
                o.rightWS = normalize(TransformObjectToWorldDir(float3(1, 0, 0)));
                o.upWS = normalize(TransformObjectToWorldDir(float3(0, 1, 0)));
                return o;
            }

            float sdRoundBox(float2 p, float2 h, float r)
            {
                float2 q = abs(p) - h + r;
                return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
            }

            float smin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(b, a, h) - k * h * (1 - h);
            }

            // The panel grows out of the orb: size and centre interpolate from inside the orb to their final values.
            float panelSdf(float2 p, out float2 halfSize)
            {
                float g = _Grow;
                halfSize = lerp(_Orb.xx * 0.45, _Body.xy * 0.5, g);
                float2 c = lerp(float2(0, 0), _BodyCenter.xy, g);
                float r = min(lerp(_Orb.x * 0.45, _Body.z, g), min(halfSize.x, halfSize.y));
                return sdRoundBox(p - c, halfSize, r);
            }

            float shapeSdf(float2 p)
            {
                float2 hs;
                float orb = length(p) - _Orb.x;
                float panel = panelSdf(p, hs);
                return smin(orb, panel, _Orb.y * saturate(_Grow * 3));
            }

            // 12-tap spiral disk blur, turned per pixel: a wide frosted blur without a separate blur pass. The
            // per-pixel turn trades the ghost copies a fixed tap pattern leaves for a fine frosted grain.
            float3 SampleBackdrop(float2 uv, float blur, float2 pixel)
            {
                float turn = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) * 6.2831853;
                float3 c = 0;
                [unroll] for (int j = 0; j < 12; j++)
                {
                    float a = j * 2.3999632 + turn;
                    float r = sqrt((j + 0.5) / 12.0);
                    c += SAMPLE_TEXTURE2D(_PtCamTex, sampler_PtCamTex, uv + float2(cos(a), sin(a)) * r * blur).rgb;
                }
                return c / 12.0;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = i.p;
                float d = shapeSdf(p);
                float aa = max(fwidth(d), 1e-5);
                float shape = saturate(0.5 - d / aa);
                // Drop shadow: the shape moved down a little, blurred; faded out before the quad's edge.
                float2 toEdge = _Quad.zw * 0.5 - abs(p - _Quad.xy);
                float shadow = 0.22 * _Glass * exp(-max(shapeSdf(p + float2(0, 0.004)), 0) / 0.005)
                             * saturate(min(toEdge.x, toEdge.y) / 0.004);
                if (shape <= 0 && shadow < 0.002) discard;

                // ---- Normal: dome over the orb, bevelled rim over the panel ----
                float2 e = float2(0.0005, 0);
                float2 grad = normalize(float2(shapeSdf(p + e.xy) - shapeSdf(p - e.xy),
                                               shapeSdf(p + e.yx) - shapeSdf(p - e.yx)) + 1e-6);
                float bevelW = _Body.w;
                float bevel = saturate(1 - (-d) / bevelW);                  // 0 deep inside → 1 at the rim
                float3 n = normalize(float3(grad * bevel * 1.3, 1 - bevel * 0.55));
                float orbR = length(p) / _Orb.x;
                if (orbR < 1)
                {
                    float3 dome = normalize(float3(p / _Orb.x, sqrt(saturate(1 - orbR * orbR))));
                    float w = saturate((1 - orbR) * 3) * saturate(1.2 - _Grow * 0.4);
                    n = normalize(lerp(n, dome, w));
                }

                // Normal in world space (quad faces the viewer: +z of the normal points at the viewer).
                float3 V = normalize(i.viewWS);
                float3 nWS = normalize(i.rightWS * n.x + i.upWS * n.y + V * n.z);

                // ---- Backdrop through the glass ----
                float3 col;
                half baseA;
                // How far into the rim band we are (1 at the edge → 0 a rim's width in): where clear glass bends light.
                float lens = saturate(1 - (-d) / max(bevelW * 1.6, 1e-4));
                if (_Clear > 0.5)
                {
                    // Clear (iOS 26 Liquid Glass): the real passthrough stays visible and sharp, only darkened a little
                    // and lifted by a touch of white (the kit's DARKEN/LUMINOSITY + 8 % LINEAR_DODGE). The tint fills in
                    // over that (a solid tint = an opaque button). Only the rim band shows the camera image, bent.
                    half dim = _ClearDim * (1 - _Tint.a);
                    half lift = 0.03;
                    baseA = 1 - (1 - dim) * (1 - _Tint.a);
                    col = (lift * (1 - _Tint.a) + _Tint.rgb * _Tint.a) / max(baseA, 1e-3);
                    if (_PtCamEnabled > 0.5 && lens > 0.01)
                    {
                        float4 pc = mul(_PtWorldToCam, float4(i.positionWS, 1));
                        float2 uv = float2(pc.x / pc.z * _PtIntrinsics.x + _PtIntrinsics.z,
                                           pc.y / pc.z * _PtIntrinsics.y + _PtIntrinsics.w);
                        float2 off = -n.xy * _Refraction * 2.5 * lens;
                        float3 bent = SampleBackdrop(uv + off, _BlurRadius, i.positionCS.xy);
                        bent = lerp(bent, _Tint.rgb, _Tint.a) * (1 - dim * 0.5) + lift;
                        float w = lens * lens * 0.9;
                        col = lerp(col * baseA, bent, w) / max(lerp(baseA, 1, w), 1e-3);
                        baseA = lerp(baseA, 1, w);
                    }
                    // Inner glow: light gathers along the inside of the edge (the kit's big soft white inner shadow).
                    // Kept to the rim band: spread wider it turns the whole pane milky.
                    float glowIn = exp(-max(-d, 0) / max(bevelW * 1.2, 1e-4));
                    col = (col * baseA + 0.22 * glowIn) / max(saturate(baseA + 0.14 * glowIn), 1e-3);
                    baseA = saturate(baseA + 0.14 * glowIn);
                }
                else if (_PtCamEnabled > 0.5)
                {
                    float4 pc = mul(_PtWorldToCam, float4(i.positionWS, 1));
                    float2 uv = float2(pc.x / pc.z * _PtIntrinsics.x + _PtIntrinsics.z,
                                       pc.y / pc.z * _PtIntrinsics.y + _PtIntrinsics.w);
                    float2 off = -n.xy * _Refraction * (0.35 + bevel);       // edges bend more
                    float2 disp = n.xy * _Dispersion * bevel;
                    col = SampleBackdrop(uv + off, _BlurRadius, i.positionCS.xy);
                    // Chromatic fringe only where the rim bends the light.
                    if (bevel > 0.01)
                    {
                        col.r = lerp(col.r, SAMPLE_TEXTURE2D(_PtCamTex, sampler_PtCamTex, uv + off - disp).r, bevel * 0.6);
                        col.b = lerp(col.b, SAMPLE_TEXTURE2D(_PtCamTex, sampler_PtCamTex, uv + off + disp).b, bevel * 0.6);
                    }
                    col = lerp(col, _Tint.rgb, _Tint.a);
                    // Adapt to the backdrop: bright → dim (and a touch more opaque) so white text keeps its
                    // contrast; dark → lift slightly so the glass doesn't vanish.
                    float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
                    float bright = smoothstep(0.45, 0.9, luma) * _Adapt;
                    float dark = (1 - smoothstep(0.05, 0.25, luma)) * _Adapt;
                    col = col * (1 - 0.45 * bright) + 0.06 * dark;
                    baseA = saturate(_PtStrength + 0.1 * bright);
                }
                else
                {
                    col = _Frost.rgb + (p.y - _BodyCenter.y) * 0.8;          // subtle top-light gradient
                    baseA = _Frost.a;
                }

                // ---- Light: specular glint + Fresnel rim (Schlick) ----
                // Light fixed in the room (not to the bubble): the glint moves as the bubble turns to follow you.
                float3 Lws = normalize(_LightDir.xyz);
                float3 H = normalize(Lws + V);
                float spec = pow(saturate(dot(nWS, H)), 90) * 2.2;
                float NdotV = saturate(dot(nWS, V));
                float fresnel = 0.04 + 0.96 * pow(1 - NdotV, 5);
                // Rim brightest on the side facing the room light (its direction in the bubble's plane).
                float2 Lp = normalize(float2(dot(Lws, i.rightWS), dot(Lws, i.upWS)) + 1e-5);
                // (only near the rim: deep inside, the SDF gradient flips along the panel's diagonals and would
                // draw faint seams from the corners)
                float rimLight = lerp(0.7, saturate(0.35 + 0.65 * dot(normalize(grad), Lp)), saturate(bevel * 2));
                float edge = saturate(1 - abs(d) / max(0.0006, aa * 1.5));

                col += spec;
                // (a matte panel, _Glass low, keeps only a trace of it: face-on it is a white veil over the whole pane)
                col = lerp(col, float3(1, 1, 1), saturate(fresnel * 1.6) * (0.4 + 0.6 * rimLight) * (1 - 0.5 * _Clear) * (0.15 + 0.85 * _Glass));
                col = lerp(col, float3(1, 1, 1), edge * 0.55 * rimLight);
                // inner dark line just inside the rim gives the glass thickness
                float inner = saturate(1 - abs(d + bevelW * 0.9) / (bevelW * 0.35));
                col *= 1 - inner * 0.12;

                // ---- Glass-button finish ----
                float2 hs;
                panelSdf(p, hs);
                float2 q = (p - lerp(float2(0, 0), _BodyCenter.xy, _Grow)) / max(hs, 1e-4);   // -1..1 across the panel
                // Sheen: CSS linear-gradient(-75deg …) runs towards the left and a little up.
                float t = dot(q, float2(-0.966, 0.259)) * 0.5;
                float sheen = lerp(0.05, 0.22, saturate(1 - abs(t) * 2));
                col = lerp(col, float3(1, 1, 1), sheen * 0.55 * _Glass * (1 - 0.8 * _Clear));
                // Inner glow along the bottom edge (white), a faint shade along the top.
                float inside = exp(-max(-d, 0) / 0.0035);
                col = lerp(col, float3(1, 1, 1), inside * saturate(-q.y) * 0.3 * _Glass);
                col *= 1 - inside * saturate(q.y) * 0.06 * _Glass;
                // Rim: 50% white, darkened at two opposite turns of a conic gradient starting at -75°.
                float turn = frac((90 - degrees(atan2(q.y, q.x)) + 75) / 360);
                float m = min(abs(turn - 0.5), min(turn, 1 - turn));
                float conic = 0.5 * (1 - smoothstep(0.0, 0.1, m));
                float rim = saturate(1 - abs(d + 0.0007) / 0.0007) * _Glass;
                col = lerp(col, lerp(float3(1, 1, 1), float3(0, 0, 0), conic) , rim * 0.5);

                // Pointer glow (visionOS-style hover): soft light inside the glass under where you point.
                float2 hp = p - _Hover.xy;
                float glow = _Hover.w * exp(-dot(hp, hp) / max(_Hover.z * _Hover.z, 1e-6)) * saturate(-d / 0.002);
                col = lerp(col, float3(1, 1, 1), glow * 0.35);

                half a = saturate(baseA + glow * 0.2 + spec * 0.6 + fresnel * 0.5 * (1 - 0.6 * _Clear) + edge * 0.4 + rim * 0.25) * shape;
                // Outside the glass only the shadow (black); at the antialiased rim the two blend.
                half outA = a + shadow * (1 - shape);
                col = outA > 1e-4 ? col * a / outA : col;
                return half4(col, outA * _Alpha);
            }
            ENDHLSL
        }
    }
}
