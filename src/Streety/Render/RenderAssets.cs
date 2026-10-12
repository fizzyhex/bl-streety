using Il2CppSLZ.Marrow.Warehouse;

using UnityEngine;

namespace Streety.Render;

/// <summary>
/// streety's own tile and sky materials, read off an internal spawnable in the streety pallet.
/// </summary>
/// <remarks>
/// the crate's prefab is never spawned; it only exists so the pallet ships the <c>fizzyhex/streety/*</c> shaders.
/// each child's (disabled) renderer holds one material, found by the child's name. without the pallet everything
/// falls back to litmas and slz's sky.
/// </remarks>
public sealed class RenderAssets
{
    public const string Barcode = "Fizzyhex.streety.Spawnable.StreetyRenderAssets";

    private const float TimeoutSeconds = 10f;

    private readonly float _started = Time.realtimeSinceStartup;
    private bool _loaded;

    public Material Tile { get; private set; }
    public Material Sky { get; private set; }

    /// <summary>
    /// true once the assets loaded, failed or timed out; streaming waits on this so tiles don't mix materials.
    /// </summary>
    public bool Settled => _loaded || Time.realtimeSinceStartup - _started > TimeoutSeconds;

    public RenderAssets()
    {
        var crate = new SpawnableCrateReference(Barcode).Crate;

        if (crate == null)
        {
            StreetyMod.Log.Warning($"missing {Barcode}; is the streety pallet up to date? using litmas and slz's sky");
            _loaded = true;
            return;
        }

        crate.MainGameObject.LoadAsset((Action<GameObject>)(prefab =>
        {
            _loaded = true;

            if (prefab == null)
            {
                StreetyMod.Log.Warning($"couldn't load {Barcode}");
                return;
            }

            Tile = FindMaterial(prefab, "Tile");
            Sky = FindMaterial(prefab, "Sky");

            StreetyMod.Log.Msg($"render assets: tile {Tile?.shader.name ?? "missing"}, sky {Sky?.shader.name ?? "missing"}");
        }));
    }

    private static Material FindMaterial(GameObject prefab, string child)
    {
        var holder = prefab.transform.Find(child);
        var renderer = holder != null ? holder.GetComponent<Renderer>() : null;
        var material = renderer != null ? renderer.sharedMaterial : null;

        return material != null && material.shader != null && material.shader.isSupported ? material : null;
    }
}
