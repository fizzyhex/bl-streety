// a planet lit only by the direction of the main directional light, so it reads the time of day without taking any
// real lighting: the day side shows the surface, the night side fades to its emission (city lights) past the terminator.
Shader "fizzyhex/streety/ShadedPlanet"
{
    Properties
    {
        [MainTexture] _BaseMap("Day", 2D) = "white" {}
        [MainColor] _BaseColor("Day Tint", Color) = (1,1,1,1)
        _EmissionMap("Night Lights", 2D) = "black" {}
        [HDR] _EmissionColor("Night Lights Tint", Color) = (1,0.85,0.6,1)
        _NightAmbient("Night Side Brightness", Range(0, 1)) = 0.02
        _Terminator("Terminator Softness", Range(0.001, 1)) = 0.2
        [HDR] _RimColor("Atmosphere", Color) = (0.35,0.6,1,1)
        _RimPower("Atmosphere Falloff", Range(0.5, 16)) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_EmissionMap);
        SAMPLER(sampler_EmissionMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _EmissionMap_ST;
            half4 _BaseColor;
            half4 _EmissionColor;
            half _NightAmbient;
            half _Terminator;
            half4 _RimColor;
            half _RimPower;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // single pass instanced stereo needs sm3.5+; the default 2.5 target drops it and the planet vanishes in the headset
            #pragma target 5.0
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/PlatformCompiler.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionWS = TransformObjectToWorld(v.vertex.xyz);
                o.vertex = TransformWorldToHClip(o.positionWS);
                o.uv = v.uv;
                o.normalWS = TransformObjectToWorldNormal(v.normal);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                half3 normalWS = normalize(i.normalWS);
                half3 viewWS = normalize(GetWorldSpaceViewDir(i.positionWS));
                half3 lightDir = GetMainLight().direction;

                // 1 in full daylight, 0 in full night, blended across a band either side of the terminator
                half ndl = dot(normalWS, lightDir);
                half day = smoothstep(-_Terminator, _Terminator, ndl);

                half3 dayColour = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(i.uv, _BaseMap)).rgb * _BaseColor.rgb;
                half3 lights = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, TRANSFORM_TEX(i.uv, _EmissionMap)).rgb * _EmissionColor.rgb;

                half3 colour = dayColour * lerp(_NightAmbient, saturate(ndl * 0.5 + 0.5), day);
                // lights only switch on once the sun has properly set
                colour += lights * (1 - smoothstep(-_Terminator * 0.5, _Terminator * 0.5, ndl + _Terminator * 0.5));

                // thin atmosphere along the limb, only where it's catching the sun
                half rim = pow(1 - saturate(dot(normalWS, viewWS)), _RimPower);
                colour += _RimColor.rgb * rim * smoothstep(-_Terminator, 1, ndl);

                return half4(colour, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/PlatformCompiler.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/PlatformCompiler.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/EncodeNormalsTexture.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normal);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return half4(EncodeWSNormalForNormalsTex(normalize(i.normalWS)), 0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
