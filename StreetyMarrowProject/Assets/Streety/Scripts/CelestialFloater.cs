#if MELONLOADER
using MelonLoader;
#endif

using UnityEngine;

namespace Streety.Marrow
{
    /// <summary>
    /// keeps a free (non-kinematic) body hovering a little above whatever's beneath it, using a damped spring
    /// on the vertical axis only, so it bobs gently into place instead of snapping.
    /// </summary>
    /// <remarks>
    /// shared between the sdk project and the mod, like <see cref="CelestialMaster"/>. the spring backs off
    /// once the body has settled so it can still fall asleep.
    /// </remarks>
#if MELONLOADER
    [RegisterTypeInIl2Cpp]
#else
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
#endif
    public class CelestialFloater : MonoBehaviour
    {
#if MELONLOADER
        public CelestialFloater(IntPtr intPtr) : base(intPtr) { }
#endif

        // gap between the bottom of the body's colliders and the ground
        private const float HoverHeight = 0.5f;

        // how far below the hover height we'll still look for ground before giving up
        private const float ProbeReach = 2f;

        // spring stiffness (1/s^2) and damping ratio; ~0.35 gives a couple of soft, shrinking bobs before settling
        private const float Stiffness = 20f;
        private const float DampingRatio = 0.35f;

        // cap so a body shoved deep into the ground doesn't launch out of it
        private const float MaxAcceleration = 20f;

        // close enough and still enough to stop pushing, so the body can sleep
        private const float RestError = 0.005f;
        private const float RestSpeed = 0.02f;

        private Rigidbody _body;
        private Collider[] _colliders;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _colliders = GetComponentsInChildren<Collider>();
        }

        private void FixedUpdate()
        {
            if (_body == null || _body.isKinematic)
                return;

            if (!TryGetGap(out var gap))
                return;

            var error = HoverHeight - gap;
            var speed = _body.velocity.y;

            // pushing would keep waking it; let it settle
            if (Mathf.Abs(error) < RestError && Mathf.Abs(speed) < RestSpeed)
                return;

            // the body's own drag already damps it; only make up the difference (or cancel some of it vertically),
            // so the overall ratio is what we asked for whatever drag the prefab has
            var damping = 2f * DampingRatio * Mathf.Sqrt(Stiffness) - _body.drag;
            var accel = Mathf.Clamp(error * Stiffness - speed * damping, -MaxAcceleration, MaxAcceleration);

            _body.AddForce(0f, accel, 0f, ForceMode.Acceleration);
        }

        /// <summary>
        /// distance from the lowest point of our colliders down to the first surface that isn't us.
        /// </summary>
        private bool TryGetGap(out float gap)
        {
            gap = 0f;

            var bottom = float.PositiveInfinity;

            foreach (var collider in _colliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger)
                    continue;

                bottom = Mathf.Min(bottom, collider.bounds.min.y);
            }

            // no solid colliders; float the pivot instead
            if (float.IsPositiveInfinity(bottom))
                bottom = _body.worldCenterOfMass.y;

            // cast from the centre of mass, so we still find the ground if we've sunk into it
            var origin = _body.worldCenterOfMass;
            var reach = origin.y - bottom + HoverHeight + ProbeReach;
            var hits = Physics.RaycastAll(origin, Vector3.down, reach, ~0, QueryTriggerInteraction.Ignore);

            var nearest = float.PositiveInfinity;

            for (var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];

                if (hit.rigidbody == _body)
                    continue;

                nearest = Mathf.Min(nearest, hit.distance);
            }

            if (float.IsPositiveInfinity(nearest))
                return false;

            gap = bottom - (origin.y - nearest);
            return true;
        }
    }
}
