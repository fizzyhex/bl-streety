using UnityEngine;

namespace Streety.Render;

/// <summary>
/// finds the material tiles are drawn with.
/// </summary>
/// <remarks>
/// the streety level can provide it: a renderer on a GameObject named <see cref="TemplateObjectName"/>, whose
/// material is cloned per tile and whose layer the tiles use. google's imagery has lighting baked in, so an
/// unlit material looks right.
/// </remarks>
public static class TileMaterials
{
    public const string TemplateObjectName = "StreetyTileMaterial";

    private const string SlzUnlit = "SLZ/SLZ Unlit";
    private const string LitMas = "SLZ/LitMAS/LitMAS Standard";

    // both slz shaders ship with the game. legacy Unlit/Texture is in the build too but draws nothing under
    // bonelab's urp fork.
    private static readonly string[] FallbackShaders =
    {
        SlzUnlit,
        LitMas,
        "Universal Render Pipeline/Lit",
    };

    public static (Material template, int layer) Resolve()
    {
        var holder = GameObject.Find(TemplateObjectName);

        if (holder != null)
        {
            var renderer = holder.GetComponent<Renderer>();

            if (renderer != null && renderer.sharedMaterial != null)
            {
                // the holder is just a reference; hide it
                renderer.enabled = false;

                StreetyMod.Log.Msg($"tile material: {renderer.sharedMaterial.name} ({renderer.sharedMaterial.shader.name})");
                return (renderer.sharedMaterial, holder.layer);
            }
        }

        foreach (var name in FallbackShaders)
        {
            var shader = FindShader(name);

            if (shader == null)
                continue;

            StreetyMod.Log.Msg($"tile shader: {name}");

            var material = new Material(shader) { name = "streety tile" };

            if (name == SlzUnlit)
                material.SetColor("_BaseColor", Color.white);
            else if (name == LitMas)
                MakeEmissive(material);

            return (material, 0);
        }

        throw new InvalidOperationException("no usable tile shader found");
    }

    /// <summary>
    /// Shader.Find only sees always-included shaders; slz unlit lives in the game's bundles, so also look through
    /// shaders that are already loaded (the rig and ui use it).
    /// </summary>
    private static Shader FindShader(string name)
    {
        var shader = Shader.Find(name);

        if (shader != null)
            return shader;

        foreach (var loaded in Resources.FindObjectsOfTypeAll<Shader>())
        {
            if (loaded.name == name && loaded.isSupported)
                return loaded;
        }

        return null;
    }

    /// <summary>
    /// litmas has no unlit mode; a black albedo with the imagery as emission keeps the baked lighting as-is.
    /// the renderer assigns each tile's texture to _EmissionMap alongside _BaseMap.
    /// </summary>
    private static void MakeEmissive(Material material)
    {
        material.SetColor("_BaseColor", Color.black);
        material.SetFloat("_Emission", 1f);
        material.SetColor("_EmissionColor", Color.white);
        material.SetFloat("_BakedMutiplier", 0f);
        material.EnableKeyword("_NO_SSR");
        material.SetFloat("_SSROff", 1f);

        // without a MAS map litmas samples its white default (fully metallic + smooth) and renders wrong
        material.SetTexture("_MetallicGlossMap", DefaultMas);
    }

    private static Texture2D _defaultMas;

    /// <summary>
    /// the sdk's MAS_M00_S10: a single linear pixel, metallic 0 / ao 1 / smoothness 11.
    /// </summary>
    private static Texture2D DefaultMas
    {
        get
        {
            if (_defaultMas != null)
                return _defaultMas;

            _defaultMas = new Texture2D(1, 1, TextureFormat.RGB24, false, true)
            {
                name = "streety MAS_M00_S10",
                filterMode = FilterMode.Point,
                // created in code, so nothing references it as far as UnloadUnusedAssets on level load knows
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };

            _defaultMas.SetPixel(0, 0, new Color32(0, 255, 11, 255));
            _defaultMas.Apply(false, true);

            return _defaultMas;
        }
    }
}
