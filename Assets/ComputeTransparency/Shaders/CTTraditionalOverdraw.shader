// The Overdraw debug view for the hardware baseline. The compute path can count its blends
// while it shades, because it owns its own loop; the hardware path cannot, so the count is
// produced by drawing the same billboards a second time into a one channel target with additive
// blending, and then mapped through the same heat ramp the compute view uses.
//
// The count draw is procedural rather than instanced through a mesh: the instances already live
// in a GraphicsBuffer, and DrawProcedural has no 1023 instance batch limit to work around.
Shader "Hidden/ComputeTransparency/Traditional Overdraw"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "TraditionalOverdrawCount"
            // Depth tested against the opaque scene exactly as the shaded draw is, so a sprite
            // hidden behind the opaque object does not count - which is what the compute path's
            // own depth test does before it increments its blend counter.
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend One One

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // The four columns of CTInstancedSpriteBatch.Instance's transform, spelled out rather
            // than declared as a float4x4: matrix storage order in a structured buffer is a
            // backend detail, column order is not.
            struct CTOverdrawInstance
            {
                float4 c0;
                float4 c1;
                float4 c2;
                float4 c3;
            };

            StructuredBuffer<CTOverdrawInstance> _CTOverdrawInstances;
            float4 _CTOverdrawOffset;

            static const uint kIndices[6] = { 0, 1, 2, 0, 2, 3 };
            static const float2 kCorners[4] =
            {
                float2(-0.5, -0.5), float2(0.5, -0.5), float2(0.5, 0.5), float2(-0.5, 0.5)
            };

            float4 Vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID) : SV_POSITION
            {
                CTOverdrawInstance instance = _CTOverdrawInstances[instanceID];

                float3 centre = instance.c3.xyz + _CTOverdrawOffset.xyz;
                float width = instance.c0.x;
                float height = instance.c1.y;

                float2 corner = kCorners[kIndices[vertexID]];

                float3 right = float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20);
                float3 up    = float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21);

                float3 positionWS = centre + right * (corner.x * width) + up * (corner.y * height);
                return TransformWorldToHClip(positionWS);
            }

            float4 Frag() : SV_Target
            {
                // One per covered fragment. The target is R32_SFloat, so the sum is exact and
                // there is no ceiling to run into.
                return float4(1, 0, 0, 0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "TraditionalOverdrawResolve"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "CTCommon.hlsl"

            float _CTDebugRange;

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float count = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0).r;
                // Opaque, like the compute debug views: the point is the count, not the scene.
                return float4(CTHeat(count / max(_CTDebugRange, 1.0)), 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
