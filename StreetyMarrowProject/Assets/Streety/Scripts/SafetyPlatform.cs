#if MELONLOADER
using MelonLoader;
#endif

using UnityEngine;

namespace Streety.Marrow
{
    /// <summary>
    /// somewhere to stand while there's no area loaded: grows out to its authored size when nothing's streamed in,
    /// and shrinks away once the ground arrives, so it never clips the tiles.
    /// </summary>
    /// <remarks>
    /// shared between the sdk project and the mod, like <see cref="CelestialMaster"/>. in the sdk it just sits at
    /// full size. only the footprint scales; the thickness stays put, so whoever's standing on it isn't dropped.
    /// purely local: each client grows or shrinks it from its own session, so fusion needs nothing synced.
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
        }

        private void OnEnable()
        {
            // no easing in on spawn; whatever's needed right now, be it
            _amount = IsNeeded() ? 1f : 0f;
            Apply();
        }

        private void Update()
        {
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

        private static bool IsNeeded()
        {
#if MELONLOADER
            // no session yet (or it couldn't start) means no ground either
            var session = StreetyMod.Session;
            return session == null || !session.HasGround;
#else
            return true;
#endif
        }
    }
}
