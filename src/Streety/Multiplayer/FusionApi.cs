using System.Runtime.CompilerServices;

using LabFusion.Network;
using LabFusion.Player;
using LabFusion.SDK.Modules;
using LabFusion.Utilities;

using Streety.Geo;

namespace Streety.Multiplayer;

/// <summary>
/// Keeps calls that reference Fusion types behind <see cref="FusionBridge"/> until Fusion is loaded.
/// </summary>
internal static class FusionApi
{
    // MethodImplOptions.NoInlining prevents JIT from loading Fusion prematurely

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsClient() => NetworkInfo.IsClient;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsHost() => NetworkInfo.IsHost;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Register() => ModuleManager.RegisterModule<StreetyModule>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendRequest(string query)
    {
        var data = new TravelRequestData { Query = query };
        MessageRelay.RelayModule<TravelRequestMessage, TravelRequestData>(data, CommonMessageRoutes.ReliableToServer);
    }

    /// <summary>
    /// (Host only)
    /// The message is sent to the client identified by the target's small ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendLocation(double latitude, double longitude, string name, byte? target)
    {
        var data = new LocationData { Latitude = latitude, Longitude = longitude, Name = name };
        var route = target.HasValue
            ? new MessageRoute(target.Value, NetworkChannel.Reliable)
            : CommonMessageRoutes.ReliableToOtherClients;

        MessageRelay.RelayModule<LocationSetMessage, LocationData>(data, route);
    }

    /// <summary>
    /// (Host only) Sends the ECEF ground the host's origin is centred on
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendGround(double latitude, double longitude, Double3 ground, byte? target)
    {
        var data = new GroundData
        {
            Latitude = latitude,
            Longitude = longitude,
            X = ground.X,
            Y = ground.Y,
            Z = ground.Z,
        };

        var route = target.HasValue
            ? new MessageRoute(target.Value, NetworkChannel.Reliable)
            : CommonMessageRoutes.ReliableToOtherClients;

        MessageRelay.RelayModule<GroundSetMessage, GroundData>(data, route);
    }

    /// <summary>
    /// (Host only) Sends the origin the host is currently using for a warp.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendOrigin(double latitude, double longitude, GeoOrigin origin, byte? target)
    {
        var data = new OriginData
        {
            Latitude = latitude,
            Longitude = longitude,
            OriginLatitude = origin.Latitude,
            OriginLongitude = origin.Longitude,
            OriginHeight = origin.Height,
        };

        var route = target.HasValue
            ? new MessageRoute(target.Value, NetworkChannel.Reliable)
            : CommonMessageRoutes.ReliableToOtherClients;

        MessageRelay.RelayModule<OriginSetMessage, OriginData>(data, route);
    }

    /// <summary>
    /// (Host only) Sends the current lobby location, ground and origin to a newly joined client.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnPlayerJoined(PlayerID id)
    {
        if (!NetworkInfo.IsHost || id.IsHost)
            return;

        if (StreetyMod.Session?.Target is not { } target)
            return;

        SendLocation(target.Latitude, target.Longitude, target.Name, id.SmallID);

        FusionBridge.AnnounceGround(target, id.SmallID);
        FusionBridge.AnnounceOrigin(target, id.SmallID);
    }
}
