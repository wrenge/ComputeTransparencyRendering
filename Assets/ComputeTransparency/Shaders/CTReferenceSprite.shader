// The baseline the compute rasterizer is measured against: the same billboarded quads, the
// same Texture2DArray, drawn by the hardware through URP's transparent queue with ordinary
// source-alpha blending. One instanced draw over a shared unit quad, with the instances
// already ordered back to front on the CPU, so the only thing left differing from the compute
// path is that every layer is shaded whether or not anything behind it can still be seen.
//
// The quad is turned towards the camera here rather than on the CPU, which is what lets the
// per instance data stay camera independent - the same trick the compute path's setup kernel
// uses, so neither side pays for billboarding on the CPU.
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
            // Texture2DArray needs this, and so does instancing: UnityInstancing.hlsl gates its
            // support on SHADER_TARGET, and the default target is below the bar.
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv         : TEXCOORD0;  // xy = atlas uv, z = array slice
                float4 color      : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Everything about the sprite rides in the instance transform, which is the one
                // piece of per instance data RenderMeshInstanced is guaranteed to deliver. A
                // translate plus a per axis scale leaves six elements of the upper 3x3 unused, so
                // the tint and the array slice are carried there rather than in an instanced
                // property block - see CTInstancedSpriteBatch.Instance.Create for the packing.
                float4x4 objectToWorld = GetObjectToWorldMatrix();
                float3 centre = float3(objectToWorld._m03, objectToWorld._m13, objectToWorld._m23);
                float width  = objectToWorld._m00;
                float height = objectToWorld._m11;
                float4 tint  = float4(objectToWorld._m01, objectToWorld._m02,
                                      objectToWorld._m10, objectToWorld._m12);
                float slice  = objectToWorld._m20;

                // Columns of the inverse view matrix are the camera's world space axes, which is
                // what CTSetup.compute is handed as _CTCameraRight and _CTCameraUp.
                float3 right = float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20);
                float3 up    = float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21);

                float3 positionWS = centre + right * (input.positionOS.x * width)
                                           + up    * (input.positionOS.y * height);

                Varyings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = float3(input.uv, slice);
                output.color = tint;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // The tint arrives already quantised to 8 bits per channel, exactly as the
                // compute path unpacks its RGBA8, so both paths multiply by the same numbers.
                float4 texel = SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, input.uv.xy, input.uv.z);
                return texel * input.color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
