using BoneLib.BoneMenu;

using UnityEngine;

namespace Streety.Menu;

/// <summary>
/// bonemenu page: type a place, warp there.
/// </summary>
public static class StreetyMenu
{
    private static readonly Color Accent = new(0.36f, 0.78f, 1f);
    private static readonly Color Muted = new(0.7f, 0.7f, 0.7f);

    private static string _location = string.Empty;
    private static FunctionElement _status;
    private static string _lastStatus;

    public static void Create()
    {
        var page = Page.Root.CreatePage("streety", Accent);

        page.CreateString("location", Color.white, _location, value => _location = value);
        page.CreateFunction("go", Accent, Go);
        _status = page.CreateFunction("load the streety level to explore", Muted, null);

#if DEBUG
        CreateDev(page);
#endif
    }

#if DEBUG
    private static FunctionElement _boost;
    private static string _lastBoost;

    /// <summary>
    /// debug builds only: live tuning for prefs. changes are saved to MelonPreferences.cfg straight away.
    /// </summary>
    private static void CreateDev(Page parent)
    {
        var dev = parent.CreatePage("dev", Muted);
        var nimbus = dev.CreatePage("nimbus boost", Accent);

        nimbus.CreateBool("enabled", Color.white, Prefs.NimbusBoost.Value, value => Save(Prefs.NimbusBoost, value));
        nimbus.CreateFloat("max boost (x)", Color.white, Prefs.NimbusBoostMax.Value, 5f, 1f, 500f,
            value => Save(Prefs.NimbusBoostMax, value));
        nimbus.CreateFloat("ceiling (m)", Color.white, Prefs.NimbusBoostCeiling.Value, 250f, 100f, 20000f,
            value => Save(Prefs.NimbusBoostCeiling, value));
        _boost = nimbus.CreateFunction("not flying", Muted, null);
    }

    private static void Save<T>(MelonLoader.MelonPreferences_Entry<T> entry, T value)
    {
        entry.Value = value;
        MelonLoader.MelonPreferences.Save();
    }

    private static void UpdateDev()
    {
        var nimbus = StreetyMod.Session?.Nimbus;
        var text = nimbus is { Holding: true }
            ? $"{nimbus.Height:F0}m · {nimbus.Multiplier:F1}x"
            : "not flying";

        if (_boost == null || text == _lastBoost)
            return;

        _lastBoost = text;
        _boost.ElementName = text;
    }
#endif

    private static void Go()
    {
        var session = StreetyMod.Session;

        if (session == null)
        {
            SetStatus("load the streety level first");
            return;
        }

        session.WarpTo(_location);
    }

    public static void Update()
    {
        var status = StreetyMod.Session?.Status ?? "load the streety level to explore";
        SetStatus(status);

#if DEBUG
        UpdateDev();
#endif
    }

    private static void SetStatus(string status)
    {
        if (_status == null || status == _lastStatus)
            return;

        _lastStatus = status;
        _status.ElementName = status;
    }
}
