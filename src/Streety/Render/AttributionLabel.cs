using BoneLib;

using Il2CppTMPro;

using UnityEngine;

namespace Streety.Render;

/// <summary>
/// the google maps logo and data providers for what's on screen (google requires attribution), shown beneath the
/// radial menu and crossfaded with it.
/// </summary>
/// <remarks>
/// uses the level's <see cref="LevelObjectName"/> (a canvas with a CanvasGroup, the logo and a text) when present,
/// otherwise a plain generated label. either way it hangs off an anchor under PLAYERUI that mirrors the radial
/// canvas pose each frame: a child of the canvas would be cut the moment the menu closes, so it couldn't fade out.
/// </remarks>
public sealed class AttributionLabel : IDisposable
{
    public const string LevelObjectName = "StreetyAttribution";

    private const string PlayerUiPath = "PLAYERUI";
    private const string RadialCanvasPath = "UI_POPUP/scaleOffset/CANVAS_RADIALUI";

    // radial canvas units (≈ mm); the radial buttons sit ~300 from the centre, the bottom one at y ≈ -284
    private static readonly Vector3 GeneratedOffset = new(0f, -370f, -99f);

    // the level object's canvas is itself 0.01 scale, so 80 makes one of its units 0.8 radial units. its content
    // spans x ≈ -89..154 and y ≈ -6..50 in its own units, so this centres it below the bottom button
    private static readonly Vector3 LevelOffset = new(-26f, -400f, -99f);
    private const float LevelScale = 80f;

    private const float FadeSeconds = 0.18f;
    private const float FontScale = 0.6f;
    private const float GeneratedMaxAlpha = 0.75f;

    private Transform _canvas;
    private GameObject _anchor;

    // level object
    private CanvasGroup _group;
    private TMP_Text _levelText;

    // generated fallback
    private TextMeshPro _generated;

    private float _alpha;
    private bool _failed;
    private string _attribution = string.Empty;

    public void SetText(string attribution)
    {
        _attribution = attribution ?? string.Empty;

        var text = _levelText != null ? _levelText : _generated;

        if (text != null && text.text != _attribution)
            text.text = _attribution;
    }

    /// <summary>
    /// follows the radial canvas and crossfades with it; call every frame.
    /// </summary>
    public void Update()
    {
        if (_canvas == null && !TryAttach())
            return;

        var target = _canvas.gameObject.activeInHierarchy && _attribution.Length > 0 ? 1f : 0f;
        _alpha = Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime / FadeSeconds);

        var visible = _alpha > 0f;

        if (_anchor.activeSelf != visible)
            _anchor.SetActive(visible);

        if (!visible)
            return;

        // the popup is placed when it opens; keep matching it while it's up or fading out
        var anchor = _anchor.transform;
        anchor.SetPositionAndRotation(_canvas.position, _canvas.rotation);
        anchor.localScale = Divide(_canvas.lossyScale, anchor.parent.lossyScale);

        var eased = Smooth(_alpha);

        if (_group != null)
            _group.alpha = eased;
        else
            _generated.alpha = GeneratedMaxAlpha * eased;
    }

    private bool TryAttach()
    {
        // throttle lookups until the ui rig exists
        if (_failed || Time.frameCount % 30 != 0)
            return false;

        var uiRig = Player.UIRig;

        if (uiRig == null)
            return false;

        var playerUi = uiRig.transform.Find(PlayerUiPath);
        var canvas = playerUi != null ? playerUi.Find(RadialCanvasPath) : null;

        if (canvas == null)
            return false;

        try
        {
            _anchor = new GameObject("streety attribution");
            _anchor.layer = canvas.gameObject.layer;
            _anchor.transform.SetParent(playerUi, false);

            var levelObject = GameObject.Find(LevelObjectName);

            if (levelObject != null)
                AdoptLevelObject(levelObject, canvas.gameObject.layer);
            else
                BuildGenerated(canvas);

            SetText(_attribution);
            _anchor.SetActive(false);
        }
        catch (Exception e)
        {
            _failed = true;
            StreetyMod.Log.Error($"couldn't set up attribution: {e}");
            Dispose();
            return false;
        }

        _canvas = canvas;
        return true;
    }

    private void AdoptLevelObject(GameObject levelObject, int layer)
    {
        var transform = levelObject.transform;
        transform.SetParent(_anchor.transform, false);
        transform.localPosition = LevelOffset;
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.one * LevelScale;

        SetLayer(transform, layer);

        _group = levelObject.GetComponentInChildren<CanvasGroup>(true);
        _levelText = levelObject.GetComponentInChildren<TMP_Text>(true);

        if (_group == null)
            throw new InvalidOperationException($"{LevelObjectName} has no CanvasGroup");

        // it's a readout; never block the radial menu's pointer
        _group.alpha = 0f;
        _group.interactable = false;
        _group.blocksRaycasts = false;

        if (_levelText != null)
            _levelText.enableWordWrapping = false;
    }

    private void BuildGenerated(Transform canvas)
    {
        // built while active: TextMeshPro needs Awake to have run before its font can be set
        var label = new GameObject("text")
        {
            layer = canvas.gameObject.layer,
        };

        label.transform.SetParent(_anchor.transform, false);
        label.transform.localPosition = GeneratedOffset;

        _generated = label.AddComponent<TextMeshPro>();
        _generated.alignment = TextAlignmentOptions.Center;
        _generated.enableWordWrapping = false;
        _generated.rectTransform.sizeDelta = new Vector2(900f, 60f);
        _generated.color = Color.white;

        // match the radial menu's own typeface (its material is a ui variant, so let the font pick ours)
        var style = canvas.GetComponentInChildren<TMP_Text>(true);

        if (style != null && style.font != null)
        {
            _generated.font = style.font;
            _generated.fontSize = style.fontSize * FontScale;
        }
        else
        {
            _generated.fontSize = 20f;
        }

        _generated.alpha = 0f;
    }

    private static void SetLayer(Transform transform, int layer)
    {
        transform.gameObject.layer = layer;

        for (var i = 0; i < transform.childCount; i++)
            SetLayer(transform.GetChild(i), layer);
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static Vector3 Divide(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);

    /// <summary>
    /// the anchor lives in the pooled player ui, which can outlive the level, so it's always torn down here.
    /// </summary>
    public void Dispose()
    {
        if (_anchor != null)
            UnityEngine.Object.Destroy(_anchor);

        _anchor = null;
        _canvas = null;
        _group = null;
        _levelText = null;
        _generated = null;
    }
}
