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
    }

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
    }

    private static void SetStatus(string status)
    {
        if (_status == null || status == _lastStatus)
            return;

        _lastStatus = status;
        _status.ElementName = status;
    }
}
