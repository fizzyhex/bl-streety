#if MELONLOADER
using BoneLib;

using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Zones;

using MelonLoader;
#endif

using UnityEngine;

namespace Streety.Marrow
{
    /// <summary>
    /// somewhere to stand while there's no area loaded, and to look out from on arrival: it follows the session
    /// up to the wait height, then out to a perch beside the new location, and shrinks away once the local player
    /// steps off it.
    /// </summary>
    /// <remarks>
    /// shared between the sdk project and the mod, like <see cref="CelestialMaster"/>. in the sdk it just sits at
    /// full size. only the footprint scales; the thickness stays put, so whoever's standing on it isn't dropped.
    /// stepping off is seen by the child zone's <c>ZoneEvents</c>, filtered to the local rig. purely local: each
    /// client places, grows and shrinks its own from its own session, so fusion needs nothing synced, and other
    /// players walking through it don't count.
    /// </remarks>
#if MELONLOADER
    [RegisterTypeInIl2Cpp]
#else
    [DisallowMultipleComponent]
#endif
    public class SafetyPlatform : MonoBehaviour
    {
#if MELONLOADER
        public SafetyPlatform(IntPtr intPtr) : base(intPtr) { }

        private ZoneEvents _zone;
        private Il2CppSystem.Action<MarrowEntity> _onEnter;
        private Il2CppSystem.Action<MarrowEntity> _onExit;
#endif

        // seconds to grow fully / shrink fully; quick to catch people, lazy to leave
        private const float GrowSeconds = 0.4f;
        private const float ShrinkSeconds = 1.5f;

        private Vector3 _scale;
        private float _amount;
        private Renderer[] _renderers;
        private Collider[] _colliders;

        private void Awake()
        {
            _scale = transform.localScale;
            _renderers = GetComponentsInChildren<Renderer>();
            _colliders = GetComponentsInChildren<Collider>();

#if MELONLOADER
            _zone = GetComponentInChildren<ZoneEvents>();

            if (_zone == null)
            {
                StreetyMod.Log.Warning("safety platform has no zone; it'll let go as soon as players arrive");
                return;
            }

            _onEnter = (Action<MarrowEntity>)(entity => Report(entity, true));
            _onExit = (Action<MarrowEntity>)(entity => Report(entity, false));
            _zone.onZoneEnter.add_DynamicCalls(_onEnter);
            _zone.onZoneExit.add_DynamicCalls(_onExit);
#endif
        }

#if MELONLOADER
        private void OnDestroy()
        {
            if (_zone == null)
                return;

            _zone.onZoneEnter.remove_DynamicCalls(_onEnter);
            _zone.onZoneExit.remove_DynamicCalls(_onExit);
        }

        private static void Report(MarrowEntity entity, bool inside)
        {
            // only the local rig decides when its own platform goes; remote fusion rigs pass straight through
            var local = Player.RigManager?.physicsRig?.marrowEntity;

            if (entity == null || local == null || entity.Pointer != local.Pointer)
                return;

            StreetyMod.Session?.ReportOnPlatform(inside);
        }
#endif

        private void OnEnable()
        {
            Follow();

            // no easing in on spawn; whatever's needed right now, be it
            _amount = IsNeeded() ? 1f : 0f;
            Apply();
        }

        private void Update()
        {
            Follow();

            var needed = IsNeeded();
            var target = needed ? 1f : 0f;

            if (_amount == target)
                return;

            var seconds = needed ? GrowSeconds : ShrinkSeconds;
            _amount = Mathf.MoveTowards(_amount, target, Time.deltaTime / seconds);
            Apply();
        }

        private void Apply()
        {
            var eased = Mathf.SmoothStep(0f, 1f, _amount);
            transform.localScale = new Vector3(_scale.x * eased, _scale.y, _scale.z * eased);

            // a zero-width collider is still a collider; switch it all off once it's gone
            var visible = _amount > 0f;

            foreach (var renderer in _renderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }

            foreach (var collider in _colliders)
            {
                if (collider != null)
                    collider.enabled = visible;
            }
        }

        private void Follow()
        {
#if MELONLOADER
            var surface = StreetyMod.Session?.PlatformSurface;

            if (!surface.HasValue)
                return;

            // the session tracks the top; the slab hangs its own thickness below that
            var position = surface.Value - new Vector3(0f, _scale.y * 0.5f, 0f);

            if ((transform.position - position).sqrMagnitude > 1e-6f)
                transform.position = position;
#endif
        }

        private static bool IsNeeded()
        {
#if MELONLOADER
            // no session yet (or it couldn't start) means no ground either
            var session = StreetyMod.Session;
            return session == null || session.PlatformNeeded;
#else
            return true;
#endif
        }
    }
}
