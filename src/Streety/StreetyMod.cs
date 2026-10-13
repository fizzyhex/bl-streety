using BoneLib;
using MelonLoader;

using Streety;

[assembly: MelonInfo(typeof(StreetyMod), StreetyMod.Name, StreetyMod.Version, StreetyMod.Author)]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]
[assembly: MelonAdditionalDependencies("BoneLib")]

namespace Streety;

public class StreetyMod : MelonMod
{
    public const string Name = "streety";
    public const string Version = "0.1.0";
    public const string Author = "Fizzyhex";

    public const string LevelBarcode = "Fizzyhex.streety.Level.Streety";

    public static MelonLogger.Instance Log { get; private set; }

    /// <summary>
    /// the active streaming session; only exists while the streety level is loaded.
    /// </summary>
    public static StreetySession Session { get; private set; }

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;

        Prefs.Initialize();
        Menu.StreetyMenu.Create();

        Hooking.OnLevelLoaded += OnLevelLoaded;
        Hooking.OnLevelUnloaded += OnLevelUnloaded;
    }

    private static void OnLevelLoaded(LevelInfo level)
    {
        EndSession();

        if (level.barcode != LevelBarcode && !Prefs.DevAnyLevel.Value)
            return;

        Log.Msg($"starting session in {level.title}");
        StartSession();
    }

    /// <summary>
    /// throws the running session away and starts a fresh one in the same level, e.g. once the maps key changes.
    /// </summary>
    public static void RestartSession()
    {
        if (Session == null)
            return;

        EndSession();
        StartSession();
    }

    private static void StartSession()
    {
        try
        {
            Session = new StreetySession();
        }
        catch (Exception e)
        {
            Log.Error($"couldn't start streety: {e}");
            EndSession();
        }
    }

    private static void OnLevelUnloaded() => EndSession();

    private static void EndSession()
    {
        if (Session == null)
            return;

        Session.Dispose();
        Session = null;
    }

    public override void OnUpdate()
    {
#if BONELAB_MCP_ADAPTER
        try
        {
            Development.McpAdapterBootstrap.Update();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
#endif
        try
        {
            Services.MapsKeyPaste.Update();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }

        try
        {
            Session?.Update();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }

        if (UnityEngine.Time.frameCount % 15 == 0)
            Menu.StreetyMenu.Update();
    }

    public override void OnApplicationQuit()
    {
#if BONELAB_MCP_ADAPTER
        Development.McpAdapterBootstrap.Dispose();
#endif
        EndSession();
    }
}
