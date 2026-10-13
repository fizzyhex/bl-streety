using LabFusion.SDK.Modules;
using LabFusion.Utilities;

using Module = LabFusion.SDK.Modules.Module;

namespace Streety.Multiplayer;

/// <summary>
/// Coordinates Streety's Fusion lobby state.
/// </summary>
internal sealed class StreetyModule : Module
{
    public override string Name => StreetyMod.Name;

    public override string Author => StreetyMod.Author;

    public override Version Version => Version.TryParse(StreetyMod.Version, out var version) ? version : new Version();

    protected override void OnModuleRegistered()
    {
        ModuleMessageManager.RegisterHandler<TravelRequestMessage>();
        ModuleMessageManager.RegisterHandler<LocationSetMessage>();

        MultiplayerHooking.OnPlayerJoined += FusionApi.OnPlayerJoined;
    }

    protected override void OnModuleUnregistered()
    {
        MultiplayerHooking.OnPlayerJoined -= FusionApi.OnPlayerJoined;
    }
}
