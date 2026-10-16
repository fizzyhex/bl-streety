using Streety.Geo;

namespace Streety.Multiplayer;

/// <summary>
/// the rest of the mod's only way into fusion. nothing in here names a labfusion type, so the jit never goes looking
/// for the assembly unless it's actually loaded; everything degrades to "singleplayer" when it isn't.
/// </summary>
public static class FusionBridge
{
    private static bool _available;
    private static WarpTarget _pending;

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
    /// Sends the ground the host's origin is centred on to the other players, or to one player when
    /// <paramref name="target"/> is set. Does nothing when called by a client or before the host has arrived.
    /// </summary>
    public static void AnnounceGround(WarpTarget warp, byte? target = null)
    {
        if (!IsHost || warp?.Ground is not { } ground)
            return;

        try
        {
            FusionApi.SendGround(warp.Latitude, warp.Longitude, ground, target);
        }
        catch (Exception e)
        {
            StreetyMod.Log.Warning($"couldn't announce the ground: {e.Message}");
        }
    }

    /// <summary>
    /// Sends the host's current origin to the other players, or to one player when <paramref name="target"/> is set.
    /// Does nothing when called by a client or before the origin is known.
    /// </summary>
    public static void AnnounceOrigin(WarpTarget warp, byte? target = null)
    {
        if (!IsHost || warp?.Origin is not { } origin)
            return;

        try
        {
            FusionApi.SendOrigin(warp.Latitude, warp.Longitude, origin, target);
        }
        catch (Exception e)
        {
            StreetyMod.Log.Warning($"couldn't announce the origin: {e.Message}");
        }
    }

    /// <summary>
    /// Stores a location received before a session is available for the next session to consume.
    /// </summary>
    internal static void Stash(double latitude, double longitude, string name) =>
        _pending = new WarpTarget(latitude, longitude, name);

    /// <summary>
    /// Stores the host's ground for the stashed location. Ignored when it belongs to a different warp.
    /// </summary>
    internal static void StashGround(double latitude, double longitude, Double3 ground)
    {
        if (_pending != null && _pending.Matches(latitude, longitude))
            _pending.Ground = ground;
    }

    /// <summary>
    /// Stores the host's origin for the stashed location. Ignored when it belongs to a different warp.
    /// </summary>
    internal static void StashOrigin(double latitude, double longitude, GeoOrigin origin)
    {
        if (_pending != null && _pending.Matches(latitude, longitude))
            _pending.Origin = origin;
    }

    internal static WarpTarget TakePending()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }
}
