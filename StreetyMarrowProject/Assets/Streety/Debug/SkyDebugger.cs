#if UNITY_EDITOR
using UnityEngine;

namespace Streety.Debugging
{
    /// <summary>
    /// stands in for the mod's Atmosphere (src/Streety/Render/Atmosphere.cs) so the sky shader can be looked at in the
    /// editor: same maths, cut down to a few knobs. editor only, never packed.
    /// </summary>
    [ExecuteAlways]
    public sealed class SkyDebugger : MonoBehaviour
    {
        public enum Term { All, Scatter, NightGlow, Stars, Haze, Clouds, Volumetrics, Dither }

        [Header("sun")]
        [Range(-30f, 90f)] public float elevation = -20f;
        [Range(0f, 360f)] public float azimuth = 200f;
        [Tooltip("the level's directional light intensity before the mod fades it")]
        public float sunIntensity = 1f;

        [Header("weather")]
        [Range(0f, 1f)] public float cloudCover = 0.6f;
        public float visibilityKm = 25f;
        public float scaleHeight = 1500f;
        public float cloudHeight = 1800f;
        [Range(0.5f, 2f)] public float atmosphereThickness = 1.1f;
        public Color skyTint = new(0.5f, 0.5f, 0.5f);
        public Color hazeTint = Color.white;
        public Vector2 cloudOffset;

        [Header("debugging")]
        [Tooltip("fakes foveated shading on the right half of the screen: pixels per block, 0 for off")]
        [Range(0, 8)] public int coarseBlock = 4;
        public Term term = Term.All;

        private static readonly Color DayAmbient = new(0.7f, 0.77f, 0.87f);
        private static readonly Color TwilightAmbient = new(0.42f, 0.4f, 0.5f);
        private static readonly Color NightAmbient = new(0.1f, 0.11f, 0.15f);
        private static readonly Color LightPollution = new(0.22f, 0.15f, 0.1f);
        private static readonly Color NightSky = new(0.04f, 0.05f, 0.08f);
        private static readonly Color AfterglowColour = new(0.75f, 0.42f, 0.22f);
        private static readonly Color SunColour = new(1f, 0.96f, 0.9f);

        private void OnEnable() => Apply();
        private void OnValidate() => Apply();
        private void Update() => Apply();

        private void OnDisable() => Shader.SetGlobalVector("_StreetySkyDebug", Vector4.zero);

        private void Apply()
        {
            var toSun = Quaternion.Euler(-elevation, azimuth, 0f) * Vector3.forward;
            var cover = cloudCover;

            var day = Smooth(-1f, 15f, elevation);
            var dusk = Smooth(-12f, -3f, elevation);
            var night = 1f - Smooth(-14f, -4f, elevation);
            var afterglow = Smooth(-9f, -1f, elevation) * (1f - Smooth(1f, 10f, elevation));

            var fade = Smooth(-4f, 2f, elevation);
            var sunLight = Linear(SunColour) * (sunIntensity * fade * fade * (1f - 0.65f * cover * cover));
            sunLight += Linear(AfterglowColour) * (0.5f * afterglow * (1f - cover));

            var pollution = Linear(LightPollution) * (0.4f + cover);

            var ambient = Vector4.Lerp(Linear(NightAmbient) * 0.15f + pollution * 0.3f, Linear(TwilightAmbient) * 0.5f, dusk);
            ambient = Vector4.Lerp(ambient, Linear(DayAmbient), day);

            var grey = (ambient.x * 0.2126f + ambient.y * 0.7152f + ambient.z * 0.0722f) * 0.9f;
            ambient = Vector4.Lerp(ambient, new Vector4(grey, grey, grey, 0f), 0.6f * cover);
            ambient = Vector4.Scale(ambient, Linear(hazeTint));

            var hazeSun = Vector4.Scale(sunLight * 0.35f, Linear(hazeTint));
            var extinction = 3.912f / Mathf.Max(visibilityKm * 1000f, 1f);

            Shader.SetGlobalVector("_StreetyHaze", new Vector4(extinction, 1f / scaleHeight, 0f, 150000f));
            Shader.SetGlobalVector("_StreetyHazeAmbient", new Vector4(ambient.x, ambient.y, ambient.z, 0.6f));
            Shader.SetGlobalVector("_StreetySunDirection", toSun);
            Shader.SetGlobalVector("_StreetySunColour", new Vector4(hazeSun.x, hazeSun.y, hazeSun.z, 0.5f));
            Shader.SetGlobalVector("_StreetySunLight", sunLight);

            var stars = (1f - Smooth(-14f, -6f, elevation)) * (1f - cover);
            var exposure = 1.3f * Smooth(-12f, 0f, elevation) * (1f - 0.35f * cover);
            Shader.SetGlobalVector("_StreetySky", new Vector4(exposure, atmosphereThickness, 0f, stars));
            Shader.SetGlobalVector("_StreetySkyTint", Linear(skyTint));
            Shader.SetGlobalVector("_StreetyNightSky", (Linear(NightSky) * 0.6f + pollution * 0.1f) * night);

            Shader.SetGlobalVector("_StreetyClouds", new Vector4(cover, 0.92f, cloudOffset.x, cloudOffset.y));
            Shader.SetGlobalVector("_StreetyCloudShape", new Vector4(cloudHeight, 3000f, 0.22f, 256f));
            Shader.SetGlobalMatrix("_StreetyStarRotation", Matrix4x4.Rotate(Quaternion.Euler(-50f, 30f, 0f)));

            Shader.SetGlobalVector("_StreetySkyDebug", new Vector4(coarseBlock, (int)term, 0f, 0f));
        }

        private static Vector4 Linear(Color c)
        {
            var l = c.linear;
            return new Vector4(l.r, l.g, l.b, 0f);
        }

        private static float Smooth(float from, float to, float value)
        {
            var t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }
    }
}
#endif
