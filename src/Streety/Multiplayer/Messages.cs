using LabFusion.Network;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Representation;
using LabFusion.SDK.Modules;
using LabFusion.UI.Popups;
using LabFusion.Utilities;

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

internal sealed class TravelRequestData : INetSerializable
{
    public string Query;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Query);
    }
}

/// <summary>
/// Sends travel requests from clients to the host. The host is notified and can accept the request.
/// Clients at or above the host's travel permission bypass the prompt and travel immediately.
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
/// Handles streety messages for the lobby.
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
