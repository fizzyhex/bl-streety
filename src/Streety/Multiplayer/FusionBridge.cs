namespace Streety.Multiplayer;

/// <summary>
/// the rest of the mod's only way into fusion. nothing in here names a labfusion type, so the jit never goes looking
/// for the assembly unless it's actually loaded; everything degrades to "singleplayer" when it isn't.
/// </summary>
public static class FusionBridge
{
    private static bool _available;
    private static (double Latitude, double Longitude, string Name)? _pending;

    /// <summary>
    /// represents if fusion is installed.
    /// </summary>
    public static bool Available => _available;

    /// <summary>
    /// in a lobby someone else hosts: travel is requested instead of done directly.
    /// </summary>
    public static bool IsClient => _available && FusionApi.IsClient();

    public static bool IsHost => _available && FusionApi.IsHost();

    public static void Initialize()
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "LabFusion"))
        {
            StreetyMod.Log.Msg("fusion isn't installed!");
            return;
        }

        try
        {
            FusionApi.Register();
            _available = true;
            StreetyMod.Log.Msg("fusion module registered");
        }
        catch (Exception e)
        {
            StreetyMod.Log.Warning($"couldn't register the fusion module: {e.Message}");
        }
    }

    /// <summary>
    /// Requests that the host travel to the specified place name.
    /// Returns false when no host is available to receive the request.
    /// </summary>
    public static bool RequestTravel(string query)
    {
        if (!IsClient)
            return false;

        try
        {
            FusionApi.SendRequest(query);
            return true;
        }
        catch (Exception e)
        {
            StreetyMod.Log.Warning($"couldn't send the travel request: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Broadcasts the host's destination to the other players in the lobby.
    /// (does nothing when called by a client)
    /// </summary>
    public static void Announce(double latitude, double longitude, string name)
    {
        if (!IsHost)
            return;

        try
        {
            FusionApi.SendLocation(latitude, longitude, name, null);
        }
        catch (Exception e)
        {
            StreetyMod.Log.Warning($"couldn't announce the location: {e.Message}");
        }
    }

    /// <summary>
    /// Stores a location received before a session is available.
    /// The next session consumes the pending location.
    /// </summary>
    internal static void Stash(double latitude, double longitude, string name) => _pending = (latitude, longitude, name);

    internal static bool TakePending(out double latitude, out double longitude, out string name)
    {
        latitude = _pending?.Latitude ?? 0;
        longitude = _pending?.Longitude ?? 0;
        name = _pending?.Name;

        var had = _pending.HasValue;
        _pending = null;
        return had;
    }
}
