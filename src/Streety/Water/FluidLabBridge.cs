using System.Reflection;

using UnityEngine;

namespace Streety.Water;

/// <summary>
/// optional hook into lakatrazz's fluidlab (https://github.com/Lakatrazz/FluidLab) for swimming and buoyancy.
/// (mod can run without it installed, hence the reflection route).
/// </summary>
public static class FluidLabBridge
{
    private static bool _resolved;
    private static Type _liquidVolume;
    private static MethodInfo _addComponent;

    public static bool Available
    {
        get
        {
            Resolve();
            return _liquidVolume != null;
        }
    }

    private static void Resolve()
    {
        if (_resolved)
            return;

        _resolved = true;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            _liquidVolume = assembly.GetType("FluidLab.LiquidVolume", false);

            if (_liquidVolume != null)
                break;
        }

        if (_liquidVolume == null)
            return;

        // fluidlab's own LiquidCreator uses the generic AddComponent
        _addComponent = typeof(GameObject).GetMethods()
            .First(m => m.Name == nameof(GameObject.AddComponent) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            .MakeGenericMethod(_liquidVolume);
    }

    /// <summary>
    /// makes a trigger box a liquid volume. its <paramref name="size"/> must match the box: the top is the surface.
    /// </summary>
    public static bool TryAttach(GameObject gameObject, Vector3 size, float density)
    {
        Resolve();

        if (_liquidVolume == null)
            return false;

        try
        {
            var volume = _addComponent.Invoke(gameObject, null);
            SetField(volume, "size", size);
            SetField(volume, "density", density);
            return true;
        }
        catch (Exception e)
        {
            StreetyMod.Log.Warning($"couldn't add a fluidlab liquid volume: {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    // fields are Il2CppValueField<T>, set through their Set(T)
    private static void SetField<T>(object volume, string name, T value)
    {
        var field = _liquidVolume.GetField(name)?.GetValue(volume);
        field?.GetType().GetMethod("Set", new[] { typeof(T) })?.Invoke(field, new object[] { value });
    }
}
