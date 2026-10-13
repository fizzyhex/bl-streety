using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;

using Il2CppTMPro;

using Streety.Geo;

using UnityEngine;

namespace Streety.Render;

/// <summary>
/// the world location pin from the streety pallet, stood over wherever a warp lands so new arrivals can orient
/// themselves from the safety platform. fades out once they step off.
/// </summary>
/// <remarks>
/// the prefab's a spawnable, but it's instantiated locally rather than spawned through the pool, so fusion never
/// sees it: each player gets their own pin for their own arrival. without the pallet there's just no pin.
/// </remarks>
public sealed class LocationPin : IDisposable
{
    public const string Barcode = "Fizzyhex.streety.Spawnable.WorldLocationPin";

    // seconds to fade fully in / out
    private const float FadeInSeconds = 0.6f;
    private const float FadeOutSeconds = 1.5f;

    // world scale per metre from the eye, so the label reads about the same size from anywhere
    private const float ScalePerMetre = 0.2f;

    // eye distance (m) the pin fades over when walked up to, so it never sits in someone's face
    private const float NearFadeStart = 8f;
    private const float NearFadeEnd = 3f;

    // how far the pin floats above the ground, per unit of scale
    private const float Lift = 1.2f;

    private readonly GameObject _holder;
    private GameObject _pin;
    private CanvasGroup _group;
    private TMP_Text _label;

    private string _name;
    private Double3 _ecef;
    private bool _shown;
    private float _alpha;

    public LocationPin(Transform parent)
    {
        // instantiated under an inactive holder so the pool component can go before anything wakes up
        _holder = new GameObject("streety location pin");
        _holder.transform.SetParent(parent, false);
        _holder.SetActive(false);

        var crate = new SpawnableCrateReference(Barcode).Crate;

        if (crate == null)
        {
            StreetyMod.Log.Warning($"missing {Barcode}; is the streety pallet up to date? arriving without a pin");
            return;
        }

        crate.MainGameObject.LoadAsset((Action<GameObject>)(prefab =>
        {
            if (prefab == null || _holder == null)
            {
                StreetyMod.Log.Warning($"couldn't load {Barcode}");
                return;
            }

            _pin = UnityEngine.Object.Instantiate(prefab, _holder.transform);
            _pin.transform.localScale = Vector3.one;

            var poolee = _pin.GetComponent<Poolee>();
            if (poolee != null)
                UnityEngine.Object.DestroyImmediate(poolee);

            _group = _pin.GetComponentInChildren<CanvasGroup>(true);
            _label = _pin.GetComponentInChildren<TMP_Text>(true);

            if (_name != null && _label != null)
                _label.text = _name;
        }));
    }

    /// <summary>
    /// stands the pin over <paramref name="ecef"/> and fades it in.
    /// </summary>
    public void Show(string name, Double3 ecef)
    {
        _name = name;
        _ecef = ecef;
        _shown = true;
        _alpha = 0f;

        if (_label != null)
            _label.text = name;
    }

    /// <summary>
    /// fades the pin out, or drops it straight away when <paramref name="immediate"/>.
    /// </summary>
    public void Hide(bool immediate = false)
    {
        _shown = false;

        if (immediate)
            _alpha = 0f;
    }

    public void Update(GeoOrigin origin, Vector3 eye)
    {
        var target = _shown ? 1f : 0f;
        var seconds = _shown ? FadeInSeconds : FadeOutSeconds;
        _alpha = Mathf.MoveTowards(_alpha, target, Time.deltaTime / seconds);

        var visible = _pin != null && _alpha > 0f;

        if (_holder.activeSelf != visible)
            _holder.SetActive(visible);

        if (!visible)
            return;

        // kept in ecef, so origin shifts don't leave it behind
        var ground = origin.EcefToUnity(_ecef);
        var distance = Vector3.Distance(eye, ground);
        var scale = Mathf.Max(1f, distance * ScalePerMetre);
        var position = ground + Vector3.up * (Lift * scale);

        var transform = _pin.transform;
        transform.position = position;
        transform.localScale = Vector3.one * scale;

        var facing = position - eye;
        if (facing.sqrMagnitude > 1e-4f)
            transform.rotation = Quaternion.LookRotation(facing);

        if (_group != null)
        {
            var near = Mathf.InverseLerp(NearFadeEnd, NearFadeStart, distance);
            _group.alpha = Mathf.SmoothStep(0f, 1f, _alpha) * near;
        }
    }

    public void Dispose()
    {
        if (_holder != null)
            UnityEngine.Object.Destroy(_holder);
    }
}
