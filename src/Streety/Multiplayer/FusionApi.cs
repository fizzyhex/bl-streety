using System.Runtime.CompilerServices;

using LabFusion.Network;
using LabFusion.Player;
using LabFusion.SDK.Modules;
using LabFusion.Utilities;

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
    /// The message is sent to the client identified by the target's small ID. Leave target as null to sends to every other client.
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
    /// (Host only) Sends the current lobby location to a newly joined client.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnPlayerJoined(PlayerID id)
    {
        if (!NetworkInfo.IsHost || id.IsHost)
            return;

        if (StreetyMod.Session is not { HasTarget: true } session)
            return;

        SendLocation(session.TargetLatitude, session.TargetLongitude, session.TargetName, id.SmallID);
    }
}
