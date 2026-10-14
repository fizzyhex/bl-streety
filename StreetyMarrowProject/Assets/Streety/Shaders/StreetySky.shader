// Procedural sky shader based on SLZ URP's procedural skybox.
// Adds haze for tiles, clouds, stars and a night-sky layer.
// Globals are managed by `Atmosphere.cs`.
Shader "fizzyhex/streety/Sky"
{
    Properties
    {
        _SunSize("Sun Size", Range(0, 1)) = 0.01
        _SunSizeConvergence("Sun Size Convergence", Range(1, 10)) = 5
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ DRAW_SKY_PROCEDURAL
            #pragma multi_compile_fragment _ _VOLUMETRICS_ENABLED

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/FullscreenSky.hlsl"
            #include "StreetyAtmosphere.hlsl"

            half _SunSize;
            half _SunSizeConvergence;

            // x: exposure, y: atmosphere thickness, z: unused, w: star brightness
            float4 _StreetySky;
            // rgb: sky tint (gamma, 0.5 = neutral)
            float4 _StreetySkyTint;
            // rgb: moonless night sky, brightest at the horizon
            float4 _StreetyNightSky;
            // rgb: sunlight lighting the clouds (colour * intensity of the level's sun)
            float4 _StreetySunLight;
            // x: cover 0-1, y: opacity, zw: wind offset in metres (wrapped by the mod)
            float4 _StreetyClouds;
            // x: base height above sea level (m), y: feature size (m), z: edge softness, w: noise period (cells)
            float4 _StreetyCloudShape;
            // celestial sphere -> world, turning with sidereal time
            float4x4 _StreetyStarRotation;
            // debugging only, zero in game. x: fake coarse shading on the right half of the screen, in pixels per block;
            // y: show one term (1 scatter, 2 night glow, 3 stars, 4 haze, 5 clouds, 6 volumetrics, 7 dither)
            float4 _StreetySkyDebug;

            // --- scattering (unity's procedural skybox) -----------------------------------------------------------

            static const float3 kDefaultScatteringWavelength = float3(.65, .57, .475);
            static const float3 kVariableRangeForScatteringWavelength = float3(.15, .15, .15);

            #define OUTER_RADIUS 1.025
            static const float kOuterRadius = OUTER_RADIUS;
            static const float kOuterRadius2 = OUTER_RADIUS * OUTER_RADIUS;
            static const float kInnerRadius = 1.0;
            static const float kInnerRadius2 = 1.0;
            static const float kCameraHeight = 0.0001;

            #define kRAYLEIGH (lerp(0.0, 0.0025, _StreetySky.y * _StreetySky.y))
            #define kMIE 0.0010
            #define kSUN_BRIGHTNESS 20.0
            #define kMAX_SCATTER 50.0

            static const half kHDSundiskIntensityFactor = 15.0;
            static const float kKmESun = kMIE * kSUN_BRIGHTNESS;
            static const float kKm4PI = kMIE * 4.0 * 3.14159265;
            static const float kScale = 1.0 / (OUTER_RADIUS - 1.0);
            static const float kScaleDepth = 0.25;
            static const float kScaleOverScaleDepth = (1.0 / (OUTER_RADIUS - 1.0)) / 0.25;
            static const float kSamples = 2.0;

            #define MIE_G (-0.990)
            #define MIE_G2 0.9801

            float scale(float inCos)
            {
                float x = 1.0 - inCos;
                return 0.25 * exp(-0.00287 + x * (0.459 + x * (3.83 + x * (-6.80 + x * 5.25))));
            }

            half getRayleighPhase(half3 light, half3 ray)
            {
                half eyeCos = dot(light, ray);
                return 0.75 + 0.75 * eyeCos * eyeCos;
            }

            half getMiePhase(float eyeCos, float eyeCos2)
            {
                half temp = 1.0 + MIE_G2 - 2.0 * MIE_G * eyeCos;
                temp = pow(temp, sqrt(_SunSize) * 10);
                temp = max(temp, 1.0e-4);
                return 1.5 * ((1.0 - MIE_G2) / (2.0 + MIE_G2)) * (1.0 + eyeCos2) / temp;
            }

            half calcSunAttenuation(float3 lightPos, float3 ray)
            {
                float focusedEyeCos = pow(saturate(dot(lightPos, ray)), _SunSizeConvergence);
                return getMiePhase(-focusedEyeCos, focusedEyeCos * focusedEyeCos);
            }

            // sky above the horizon; anything below looks along the horizon and gets buried in haze anyway
            void Scatter(float3 eyeRay, float3 sun, out half3 sky, out half3 sunDisc)
            {
                half3 tint = _StreetySkyTint.rgb;
                half3 wavelength = lerp(
                    kDefaultScatteringWavelength - kVariableRangeForScatteringWavelength,
                    kDefaultScatteringWavelength + kVariableRangeForScatteringWavelength,
                    half3(1, 1, 1) - tint);
                half3 invWavelength = 1.0 / pow(wavelength, 4);

                half krESun = kRAYLEIGH * kSUN_BRIGHTNESS;
                half kr4PI = kRAYLEIGH * 4.0 * 3.14159265;

                eyeRay.y = max(eyeRay.y, 0.0);
                eyeRay = normalize(eyeRay);

                float3 cameraPos = float3(0, kInnerRadius + kCameraHeight, 0);
                float far = sqrt(kOuterRadius2 + kInnerRadius2 * eyeRay.y * eyeRay.y - kInnerRadius2) - kInnerRadius * eyeRay.y;

                float sampleLength = far / kSamples;
                float scaledLength = sampleLength * kScale;
                float3 sampleRay = eyeRay * sampleLength;
                float3 samplePoint = cameraPos + sampleRay * 0.5;

                float depth = exp(-kCameraHeight * kScaleOverScaleDepth);
                float height = kInnerRadius + kCameraHeight;
                float startAngle = dot(eyeRay, cameraPos) / height;
                float startOffset = depth * scale(startAngle);

                float3 frontColor = 0;
                float3 attenuate = 1;

                [unroll] for (int i = 0; i < int(kSamples); i++)
                {
                    float sampleHeight = length(samplePoint);
                    float sampleDepth = exp(kScaleOverScaleDepth * (kInnerRadius - sampleHeight));
                    float lightAngle = dot(sun, samplePoint) / sampleHeight;
                    float cameraAngle = dot(eyeRay, samplePoint) / sampleHeight;
                    float scatter = startOffset + sampleDepth * (scale(lightAngle) - scale(cameraAngle));
                    attenuate = exp(-clamp(scatter, 0.0, kMAX_SCATTER) * (invWavelength * kr4PI + kKm4PI));

                    frontColor += attenuate * (sampleDepth * scaledLength);
                    samplePoint += sampleRay;
                }

                half3 cIn = frontColor * (invWavelength * krESun);
                half3 cOut = frontColor * kKmESun;

                sky = _StreetySky.x * cIn * getRayleighPhase(sun, eyeRay);
                sunDisc = kHDSundiskIntensityFactor * saturate(cOut) * calcSunAttenuation(sun, eyeRay);
            }

            // --- clouds & stars -----------------------------------------------------------------------------------

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Hash(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float3 Hash3(float3 p)
            {
                return float3(Hash(p), Hash(p + 17.13), Hash(p + 41.71));
            }

            // value noise that repeats every `period` cells, so the wrapped wind offset never shows a seam
            float Noise(float2 p, float period)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);

                float2 i0 = i - period * floor(i / period);
                float2 i1 = (i + 1) - period * floor((i + 1) / period);

                float a = Hash(i0);
                float b = Hash(float2(i1.x, i0.y));
                float c = Hash(float2(i0.x, i1.y));
                float d = Hash(i1);

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float Fbm(float2 p, float period)
            {
                float sum = 0;
                float amplitude = 0.5;

                [unroll] for (int i = 0; i < 5; i++)
                {
                    sum += amplitude * Noise(p, period);
                    p *= 2.0;
                    period *= 2.0;
                    amplitude *= 0.5;
                }

                return sum / 0.96875;
            }

            // returns cloud opacity; colour is lit cloud already hazed by the air between it and the eye
            float Clouds(float3 eyeRay, out float3 colour)
            {
                colour = 0;

                float cover = _StreetyClouds.x;
                float3 camera = _WorldSpaceCameraPos;
                float layer = _StreetyHaze.z + _StreetyCloudShape.x;
                float t = (layer - camera.y) / eyeRay.y;

                if (cover <= 0 || t <= 0 || _StreetyCloudShape.y <= 0)
                    return 0;

                float2 p = (camera.xz + eyeRay.xz * t + _StreetyClouds.zw) / _StreetyCloudShape.y;
                float n = Fbm(p, _StreetyCloudShape.w);
                float density = saturate((n - (1.0 - cover)) / max(_StreetyCloudShape.z, 1e-3));

                // grazing angles alias into noise; let the haze have them
                density *= smoothstep(0.0, 0.06, abs(eyeRay.y)) * _StreetyClouds.y;

                float3 sun = StreetySunDirection();
                float forward = pow(saturate(dot(eyeRay, sun)), 6.0);

                // thick cores darken, thin edges catch the sun
                float3 lit = _StreetyHazeAmbient.rgb * 1.2
                    + _StreetySunLight.rgb * (0.4 + 0.9 * forward) * (1.0 - 0.55 * density)
                    + _StreetyNightSky.rgb * 2.0;

                colour = StreetyApplyHaze(lit, camera, eyeRay, t);
                return density;
            }

            float3 Stars(float3 eyeRay)
            {
                float3 d = mul((float3x3)_StreetyStarRotation, eyeRay);
                float3 p = d * 220.0;
                float3 cell = floor(p);
                float3 jitter = Hash3(cell) * 0.6 + 0.2;
                float brightness = Hash(cell + 7.7);

                // ~2% of cells hold a star, mostly faint
                float exists = step(0.98, brightness);
                float magnitude = pow((brightness - 0.98) / 0.02, 4.0);
                float spot = smoothstep(0.3, 0.0, length(frac(p) - jitter));

                float3 hue = lerp(float3(0.75, 0.85, 1.0), float3(1.0, 0.85, 0.7), Hash(cell + 3.1));
                return exists * spot * (0.15 + 2.5 * magnitude) * hue;
            }

            // --- passes ---------------------------------------------------------------------------------------------

            struct Attributes
            {
                #if defined(DRAW_SKY_PROCEDURAL)
                    uint vertexID : SV_VertexID;
                #else
                    float4 vertex : POSITION;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float3 wPos : TEXCOORD0;
                float3 ray : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            #if defined(DRAW_SKY_PROCEDURAL)
                GetSkyVertexPos(v.vertexID, o.pos, o.wPos);
                o.ray = o.wPos - _WorldSpaceCameraPos;
            #else
                o.wPos = TransformObjectToWorld(v.vertex.xyz);
                o.pos = TransformWorldToHClip(o.wPos);
                o.ray = v.vertex.xyz;
            #endif

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // fake foveation: shade the whole block from its centre, like a variable rate shader would
                float block = _StreetySkyDebug.x;
                float2 pixel = i.pos.xy;
                float3 rayAt = i.ray;
                float3 wPos = i.wPos;

                if (block > 1 && pixel.x > _ScreenParams.x * 0.5)
                {
                    float2 offset = (floor(pixel / block) + 0.5) * block - pixel;
                    pixel += offset;
                    rayAt += ddx(i.ray) * offset.x + ddy(i.ray) * offset.y;
                    wPos += ddx(i.wPos) * offset.x + ddy(i.wPos) * offset.y;
                }

                float3 eyeRay = normalize(rayAt);
                float3 sun = StreetySunDirection();
                float3 camera = _WorldSpaceCameraPos;
                float above = saturate(eyeRay.y * 50.0);

                half3 sky, sunDisc;
                Scatter(eyeRay, sun, sky, sunDisc);
                half3 scatter = sky;

                // night: a dim sky that glows towards the horizon, and stars
                float horizonGlow = 1.0 - sqrt(saturate(eyeRay.y));
                sky += _StreetyNightSky.rgb * (1.0 + 1.5 * horizonGlow);
                sky += Stars(eyeRay) * _StreetySky.w * above;

                float transmittance = StreetyTransmittance(camera, eyeRay, _StreetyHaze.w);
                float3 colour = sky * transmittance + StreetyHazeColour(eyeRay) * (1.0 - transmittance);

                // the disc is hazed more gently than physics would, so a hazy sunset still shows a sun
                colour += sunDisc * sqrt(transmittance) * above;

                float3 cloudColour;
                float cloud = Clouds(eyeRay, cloudColour);
                colour = lerp(colour, cloudColour, cloud);

                half4 finalColour = Volumetrics(half4(colour, 1.0), wPos);

                // dither so gradients don't band. it's applied in srgb so it stays one 8-bit step at night, where a
                // linear step is many; and it's a hash rather than a 4x4 pattern, since foveated rendering shades
                // whole blocks of pixels at once and a pattern then collapses into a flat offset around the periphery
                float noise = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) - 0.5;
                float3 srgb = LinearToSRGB(max(finalColour.rgb, 0.0));
                finalColour.rgb = SRGBToLinear(max(srgb + noise / 255.0, 0.0));

                int term = (int)_StreetySkyDebug.y;
                if (term == 1) finalColour.rgb = scatter;
                else if (term == 2) finalColour.rgb = _StreetyNightSky.rgb * (1.0 + 1.5 * horizonGlow);
                else if (term == 3) finalColour.rgb = Stars(eyeRay) * _StreetySky.w * above;
                else if (term == 4) finalColour.rgb = StreetyHazeColour(eyeRay) * (1.0 - transmittance);
                else if (term == 5) finalColour.rgb = cloudColour * cloud;
                else if (term == 6) finalColour.rgb = Volumetrics(half4(0, 0, 0, 1), wPos).rgb;
                else if (term == 7) finalColour.rgb = SRGBToLinear((0.5 + noise).xxx);

                return finalColour;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
