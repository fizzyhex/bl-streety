using BoneLib;

using Il2CppSLZ.Marrow;

using Streety.Geo;

using UnityEngine;

namespace Streety.Movement;

/// <summary>
/// speeds the nimbus gun up the higher the player flies, so crossing a city from the clouds doesn't take forever.
/// </summary>
/// <remarks>
/// the boost grows exponentially from <see cref="FloorHeight"/> up to the ceiling pref, then holds.
/// </remarks>
public sealed class NimbusBoost : IDisposable
{
    // below this, the gun flies as bonelab made it
    private const float FloorHeight = 30f;

    private readonly Dictionary<int, (FlyingGun gun, float baseSpeed)> _boosted = new();
    private readonly HashSet<int> _held = new();
    private readonly List<int> _released = new();

    /// <summary>
    /// the multiplier last applied, for debugging; 1 when nothing's boosted.
    /// </summary>
    public float Multiplier { get; private set; } = 1f;

    /// <summary>
    /// the local player's height above the ground, as the boost sees it.
    /// </summary>
    public float Height { get; private set; }

    /// <summary>
    /// whether the local player is holding a nimbus gun right now.
    /// </summary>
    public bool Holding => _held.Count > 0;

    public void Update(GeoOrigin origin)
    {
        _held.Clear();

        var physicsRig = Player.RigManager?.physicsRig;
        var head = physicsRig?.m_head;

        if (Prefs.NimbusBoost.Value && head != null)
        {
            Height = HeightAboveGround(origin, head.position);
            Multiplier = Evaluate(Height);

            foreach (var hand in new[] { physicsRig.leftHand, physicsRig.rightHand })
            {
                var held = hand?.m_CurrentAttachedGO;
                var gun = held != null ? held.GetComponentInParent<FlyingGun>() : null;

                if (gun == null)
                    continue;

                var id = gun.GetInstanceID();

                if (!_held.Add(id))
                    continue;

                if (!_boosted.TryGetValue(id, out var entry))
                {
                    entry = (gun, gun.maxSpeed);
                    _boosted[id] = entry;
                }

                gun.maxSpeed = entry.baseSpeed * Multiplier;
            }
        }
        else
        {
            Multiplier = 1f;
        }

        _released.Clear();

        foreach (var id in _boosted.Keys)
        {
            if (!_held.Contains(id))
                _released.Add(id);
        }

        foreach (var id in _released)
            Restore(id);
    }

    /// <summary>
    /// metres above the ground the player last arrived on, following the curve of the earth.
    /// </summary>
    private static float HeightAboveGround(GeoOrigin origin, Vector3 position)
    {
        var (_, _, height) = Wgs84.EcefToGeodetic(origin.UnityToEcef(position));
        return (float)(height - origin.Height);
    }

    private static float Evaluate(float height)
    {
        var ceiling = Mathf.Max(Prefs.NimbusBoostCeiling.Value, FloorHeight + 1f);
        var max = Mathf.Max(Prefs.NimbusBoostMax.Value, 1f);
        var t = Mathf.InverseLerp(FloorHeight, ceiling, height);

        // max^t: 1x at the floor, max at the ceiling, and each stretch of height multiplies the speed by the same amount
        return Mathf.Pow(max, t);
    }

    private void Restore(int id)
    {
        var (gun, baseSpeed) = _boosted[id];
        _boosted.Remove(id);

        if (gun != null)
            gun.maxSpeed = baseSpeed;
    }

    public void Dispose()
    {
        foreach (var id in _boosted.Keys.ToList())
            Restore(id);

        Multiplier = 1f;
    }
}
