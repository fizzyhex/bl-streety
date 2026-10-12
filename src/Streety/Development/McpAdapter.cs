#if BONELAB_MCP_ADAPTER
using System.Runtime.CompilerServices;
using BonelabMcp.Contracts;

namespace Streety.Development;

// Keep calls into the optional assembly out of the JIT path until Contracts is loaded.
internal static class McpAdapterBootstrap
{
    public static void Update()
    {
        if (UnityEngine.Time.frameCount % 60 != 0) return;
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BonelabMcp.Contracts")) return;
        McpAdapter.RegisterIfAvailable();
    }
    public static void Dispose()
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BonelabMcp.Contracts")) McpAdapter.Dispose();
    }
}

internal static class McpAdapter
{
    private static IDisposable _warp;
    private static IDisposable _status;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RegisterIfAvailable()
    {
        if (_warp != null || !ExtensionRegistry.IsBridgeAvailable) return;
        _warp = ExtensionRegistry.Register(new("streety_warp",
            "Start Streety location warp by place name. Returns acceptance; poll streety_status until isWarping becomes false.",
            Protocol.Element(new { type = "object", properties = new { query = new { type = "string", minLength = 1 } },
                required = new[] { "query" }, additionalProperties = false }), true), args =>
        {
            var query = Arguments.String(args, "query");
            var session = StreetyMod.Session ?? throw new ToolException("streety_unavailable", "No active Streety session.");
            if (session.IsWarping) throw new ToolException("busy", "Streety is already warping.");
            session.WarpTo(query);
            if (!session.IsWarping) throw new ToolException("streety_unavailable", session.Status);
            return Protocol.Element(new { accepted = true, status = session.Status });
        });
        _status = ExtensionRegistry.Register(new("streety_status",
            "Read Streety session availability, status text, location name and isWarping.",
            Protocol.Element(new { type = "object", properties = new { }, additionalProperties = false })), _ =>
        {
            var session = StreetyMod.Session;
            return Protocol.Element(new { sessionAvailable = session != null, status = session?.Status,
                locationName = session?.LocationName, isWarping = session?.IsWarping ?? false });
        });
        StreetyMod.Log.Msg("registered Streety development MCP tools");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Dispose()
    {
        _warp?.Dispose(); _status?.Dispose(); _warp = null; _status = null;
    }
}
#endif
