// The pointing ray (a LineRenderer: u along its length, v across its width).
//
// A hairline core of light inside a soft halo: the core is sharp and nearly white, the halo a wide, faint falloff in
// the ray's colour, so the beam reads as light rather than a drawn line. The line's vertex colours carry the fade
// (out of the hand, up to the tip). _Glint runs a bright pulse along the ray when a press lands.
//
// Premultiplied, a little additive (alpha written below the colour): the light lifts what is behind it, and the Quest
// blends the app over passthrough by alpha.
Shader "MetaStarter/PointerRay"
{
    Properties
    {
        _Glint ("Glint: position along (0-1), width, strength", Vector) = (0, 0.12, 0, 0)
        _Core ("Core: half-width (0-1 of the line), halo strength, halo falloff, whiten", Vector) = (0.16, 0.35, 7, 0.7)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Pass
        {
            Name "PointerRay"
            Blend One OneMinusSrcAlpha
            ColorMask RGBA
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Glint;
                float4 _Core;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float across = abs(i.uv.y * 2.0 - 1.0);                     // 0 on the axis, 1 at the edge
                float aa = max(fwidth(across), 1e-4);
                float core = 1.0 - smoothstep(_Core.x - aa, _Core.x + aa, across);
                float halo = exp(-across * across * _Core.z) * _Core.y * (1.0 - across);
                float d = (i.uv.x - _Glint.x) / max(_Glint.y, 1e-3);
                float glint = _Glint.z * exp(-d * d);
                float a = saturate(i.color.a * (core + halo) * (1.0 + 2.0 * glint) + glint * core * 0.5);
                half3 rgb = lerp(i.color.rgb, half3(1, 1, 1), saturate(core * _Core.w + glint));
                return half4(rgb * a, a * 0.85);
            }
            ENDHLSL
        }
    }
}
