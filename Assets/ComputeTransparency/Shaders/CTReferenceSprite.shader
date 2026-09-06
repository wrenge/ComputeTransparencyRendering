// The baseline the compute rasterizer is measured against: the same quads, the same
// Texture2DArray, drawn by the hardware through URP's transparent queue with ordinary
// source-alpha blending. One draw call over a mesh whose triangles are already ordered
// back to front, so the only thing left differing from the compute path is that every
// layer is shaded whether or not anything behind it can still be seen.
Shader "ComputeTransparency/Reference Sprite"
{
    Properties
    {
        [NoScaleOffset] _BaseMap ("Base Map", 2DArray) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ReferenceSprite"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 uv         : TEXCOORD0;  // xy = atlas uv, z = array slice
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Vertex colours arrive as raw UNorm8, exactly as the compute path unpacks
                // its RGBA8 tint, so both paths multiply by the same numbers.
                float4 texel = SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, input.uv.xy, input.uv.z);
                return texel * input.color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
