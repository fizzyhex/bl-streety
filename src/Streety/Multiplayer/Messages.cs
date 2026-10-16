using LabFusion.Network;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Representation;
using LabFusion.SDK.Modules;
using LabFusion.UI.Popups;
using LabFusion.Utilities;

using Streety.Geo;

using UnityEngine;

namespace Streety.Multiplayer;

/// <summary>
/// Contains a resolved place so clients do not need to geocode or have a maps API key.
/// </summary>
internal sealed class LocationData : INetSerializable
{
    public double Latitude;
    public double Longitude;
    public string Name;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Latitude);
        serializer.SerializeValue(ref Longitude);
        serializer.SerializeValue(ref Name);
    }
}

/// <summary>
/// Contains the host's ECEF ground for a warp. The warp's coordinates are included so clients can ignore a ground
/// that belongs to a different warp.
/// </summary>
internal sealed class GroundData : INetSerializable
{
    public double Latitude;
    public double Longitude;
    public double X;
    public double Y;
    public double Z;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Latitude);
        serializer.SerializeValue(ref Longitude);
        serializer.SerializeValue(ref X);
        serializer.SerializeValue(ref Y);
        serializer.SerializeValue(ref Z);
    }
}

/// <summary>
/// Contains the host's current origin for a warp, tagged with the warp's coordinates like <see cref="GroundData"/>.
/// </summary>
internal sealed class OriginData : INetSerializable
{
    public double Latitude;
    public double Longitude;
    public double OriginLatitude;
    public double OriginLongitude;
    public double OriginHeight;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Latitude);
        serializer.SerializeValue(ref Longitude);
        serializer.SerializeValue(ref OriginLatitude);
        serializer.SerializeValue(ref OriginLongitude);
        serializer.SerializeValue(ref OriginHeight);
    }
}

internal sealed class TravelRequestData : INetSerializable
{
    public string Query;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Query);
    }
}

/// <summary>
/// Sends travel requests from clients to the host.
/// Clients at/above the host's travel permission bypass the prompt.
/// </summary>
internal sealed class TravelRequestMessage : ModuleMessageHandler
{
    private const float RequestCooldown = 10f;
    private static float _timeOfRequest = -1000f;

    // above owner, so only the host ever clears it
    private const PermissionLevel HostOnly = (PermissionLevel)3;

    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ServerOnly;

    // note: this is totally ripping off fusion's permission system.
    // I think it makes sense to mirror it :P

    private static PermissionLevel RequiredLevel() => Prefs.TravelPermission.Value?.Trim().ToLowerInvariant() switch
    {
        "host" => HostOnly,
        "owner" => PermissionLevel.OWNER,
        "everyone" => PermissionLevel.GUEST,
        _ => PermissionLevel.OPERATOR,
    };

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        if (!received.Sender.HasValue)
            return;

        var data = received.ReadData<TravelRequestData>();

        if (string.IsNullOrWhiteSpace(data.Query))
            return;

        var id = PlayerIDManager.GetPlayerID(received.Sender.Value);

        if (id == null)
            return;

        var query = data.Query.Trim();

        FusionPermissions.FetchPermissionLevel(id.PlatformID, out var level, out _);

        if (FusionPermissions.HasSufficientPermissions(level, RequiredLevel()))
        {
            StreetyMod.Session?.WarpToLocal(query);
            return;
        }

        // no request spamming
        if (TimeReferences.TimeSinceStartup - _timeOfRequest <= RequestCooldown)
            return;

        if (!id.TryGetDisplayName(out var name))
            return;

        _timeOfRequest = TimeReferences.TimeSinceStartup;

        Notifier.Send(new Notification
        {
            Title = "travel request",
            Message = new NotificationText($"{name} wants to go to {query}.", Color.yellow),

            SaveToMenu = true,
            ShowPopup = true,
            OnAccepted = () => StreetyMod.Session?.WarpToLocal(query),
        });
    }
}

/// <summary>
/// Handles location messages for the lobby.
/// </summary>
[Net.DelayWhileLoading]
internal sealed class LocationSetMessage : ModuleMessageHandler
{
    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ClientsOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<LocationData>();
        var session = StreetyMod.Session;

        if (session == null)
        {
            FusionBridge.Stash(data.Latitude, data.Longitude, data.Name);
            return;
        }

        session.WarpToResolved(data.Latitude, data.Longitude, data.Name);
    }
}


/// <summary>
/// Handles the host's ground for the lobby, so clients centre their origin where the host's is.
/// </summary>
[Net.DelayWhileLoading]
internal sealed class GroundSetMessage : ModuleMessageHandler
{
    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ClientsOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<GroundData>();
        var ground = new Double3(data.X, data.Y, data.Z);
        var session = StreetyMod.Session;

        if (session == null)
        {
            FusionBridge.StashGround(data.Latitude, data.Longitude, ground);
            return;
        }

        session.ReceiveGround(data.Latitude, data.Longitude, ground);
    }
}

/// <summary>
/// Handles the host's origin after a warp or rebase, so every peer keeps the same Unity space.
/// </summary>
[Net.DelayWhileLoading]
internal sealed class OriginSetMessage : ModuleMessageHandler
{
    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ClientsOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<OriginData>();
        var origin = new GeoOrigin(data.OriginLatitude, data.OriginLongitude, data.OriginHeight);
        var session = StreetyMod.Session;

        if (session == null)
        {
            FusionBridge.StashOrigin(data.Latitude, data.Longitude, origin);
            return;
        }

        session.ReceiveOrigin(data.Latitude, data.Longitude, origin);
    }
}
