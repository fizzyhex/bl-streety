using MelonLoader;

namespace Streety;

public static class Prefs
{
    private static MelonPreferences_Category _category;

    public static MelonPreferences_Entry<string> MapsKey { get; private set; }
    public static MelonPreferences_Entry<string> DevEnvPath { get; private set; }
    public static MelonPreferences_Entry<float> MaxScreenSpaceError { get; private set; }
    public static MelonPreferences_Entry<int> MaxConcurrentRequests { get; private set; }
    public static MelonPreferences_Entry<float> UploadBudgetMs { get; private set; }
    public static MelonPreferences_Entry<int> MemoryBudgetMb { get; private set; }
    public static MelonPreferences_Entry<float> ColliderRadius { get; private set; }
    public static MelonPreferences_Entry<double> StartLatitude { get; private set; }
    public static MelonPreferences_Entry<double> StartLongitude { get; private set; }
    public static MelonPreferences_Entry<string> StartName { get; private set; }
    public static MelonPreferences_Entry<float> FarClip { get; private set; }
    public static MelonPreferences_Entry<float> RebaseDistance { get; private set; }
    public static MelonPreferences_Entry<bool> DevAnyLevel { get; private set; }
    public static MelonPreferences_Entry<float> MusicVolume { get; private set; }
    public static MelonPreferences_Entry<float> SunHourOffset { get; private set; }
    public static MelonPreferences_Entry<float> FogVisibilityKm { get; private set; }
    public static MelonPreferences_Entry<bool> Water { get; private set; }
    public static MelonPreferences_Entry<string> OverpassUrls { get; private set; }
    public static MelonPreferences_Entry<bool> NavMesh { get; private set; }
    public static MelonPreferences_Entry<float> NavMeshRadius { get; private set; }
    public static MelonPreferences_Entry<bool> NimbusBoost { get; private set; }
    public static MelonPreferences_Entry<float> NimbusBoostMax { get; private set; }
    public static MelonPreferences_Entry<float> NimbusBoostCeiling { get; private set; }

    public static void Initialize()
    {
        _category = MelonPreferences.CreateCategory("streety");

        MapsKey = _category.CreateEntry("maps_key", "", "google maps platform api key",
            "needs the map tiles api + geocoding api enabled");
        DevEnvPath = _category.CreateEntry("dev_env_path", "",
            "dev .env path", "optional; read MAPS_TOKEN from this .env when maps_key is empty");
        MaxScreenSpaceError = _category.CreateEntry("max_screen_space_error", 16f,
            "max screen space error", "lower = sharper and more requests");
        MaxConcurrentRequests = _category.CreateEntry("max_concurrent_requests", 12, "max concurrent requests");
        UploadBudgetMs = _category.CreateEntry("upload_budget_ms", 2f,
            "upload budget (ms/frame)", "main-thread time spent creating meshes/textures each frame");
        MemoryBudgetMb = _category.CreateEntry("memory_budget_mb", 2048, "tile memory budget (mb)");
        ColliderRadius = _category.CreateEntry("collider_radius", 150f,
            "collider radius (m)", "tiles within this distance of a rig get collision");
        StartLatitude = _category.CreateEntry("start_latitude", 48.858370, "start latitude");
        StartLongitude = _category.CreateEntry("start_longitude", 2.294481, "start longitude");
        StartName = _category.CreateEntry("start_name", "Eiffel Tower", "start location name");
        FarClip = _category.CreateEntry("far_clip", 20000f, "camera far clip (m)");
        RebaseDistance = _category.CreateEntry("rebase_distance", 4000f,
            "origin rebase distance (m)", "recentre the world when a player gets this far from the origin");
        DevAnyLevel = _category.CreateEntry("dev_any_level", false,
            "dev: stream in any level", "start streety in every level, not just the streety level");
        MusicVolume = _category.CreateEntry("music_volume", 0.5f,
            "music volume", "0-1; sits under bonelab's own music volume");
        SunHourOffset = _category.CreateEntry("sun_hour_offset", 0f,
            "sun hour offset", "shifts the sun's clock by this many hours; 0 = the real time at the location");
        FogVisibilityKm = _category.CreateEntry("fog_visibility_km", 0f,
            "fog visibility (km)", "how far you can see at sea level; 0 = from the region and the weather");
        Water = _category.CreateEntry("water", true,
            "swimmable water", "needs fluidlab; finds water from openstreetmap and lets you swim in it");
        OverpassUrls = _category.CreateEntry("overpass_urls",
            "https://overpass-api.de/api/interpreter, https://maps.mail.ru/osm/tools/overpass/api/interpreter, https://overpass.kumi.systems/api/interpreter, https://overpass.private.coffee/api/interpreter",
            "overpass urls", "where openstreetmap water is looked up, first choice first; a busy one hands over to the next. results are cached for a month");
        NavMesh = _category.CreateEntry("navmesh", true,
            "npc navmesh", "bakes walkable ground around each player so npcs can move");
        NavMeshRadius = _category.CreateEntry("navmesh_radius", 100f,
            "navmesh radius (m)", "how far around a player npcs can walk; keep it within the collider radius");
        NimbusBoost = _category.CreateEntry("nimbus_boost", true,
            "nimbus altitude boost", "the nimbus gun flies faster the higher you are");
        NimbusBoostMax = _category.CreateEntry("nimbus_boost_max", 40f,
            "nimbus max boost", "speed multiplier at the boost ceiling; it ramps up exponentially on the way");
        NimbusBoostCeiling = _category.CreateEntry("nimbus_boost_ceiling", 5000f,
            "nimbus boost ceiling (m)", "height above the ground where the boost stops growing");
    }

    /// <summary>
    /// resolves the api key without ever logging it.
    /// </summary>
    public static string ResolveMapsKey()
    {
        var key = MapsKey.Value?.Trim();

        if (!string.IsNullOrEmpty(key))
            return key;

        var envPath = DevEnvPath.Value?.Trim();

        if (string.IsNullOrEmpty(envPath) || !File.Exists(envPath))
            return null;

        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith('#') || !trimmed.StartsWith("MAPS_TOKEN", StringComparison.Ordinal))
                continue;

            var split = trimmed.IndexOf('=');

            if (split < 0)
                continue;

            return trimmed[(split + 1)..].Trim().Trim('"');
        }

        return null;
    }
}
