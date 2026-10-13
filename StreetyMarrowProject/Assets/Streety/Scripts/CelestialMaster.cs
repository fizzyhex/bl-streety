#if MELONLOADER
using MelonLoader;
#endif

using UnityEngine;

namespace Streety.Marrow
{
    /// <summary>
    /// lets a grabbable globe steer the scene's sun. it has two modes:
    /// <list type="bullet">
    /// <item><b>sleeping</b> (default): the body moves freely, and torques itself round to mirror wherever the
    /// light currently points; it never touches the sun.</item>
    /// <item><b>active</b> (<see cref="Activate"/>): the body is pinned in place but free to pivot, and the
    /// directional light follows its rotation. <see cref="Sleep"/> hands it back to sleeping.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// shared between the sdk project and the mod (linked into Streety.csproj), the same way fusion's marrow
    /// integration works: the mod registers it in il2cpp, and the bundle's script reference resolves to it by
    /// namespace and name. keep the type name, namespace and public surface identical on both sides.
    /// </remarks>
#if MELONLOADER
    [RegisterTypeInIl2Cpp]
#else
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
#endif
    public class CelestialMaster : MonoBehaviour
    {
#if MELONLOADER
        public CelestialMaster(IntPtr intPtr) : base(intPtr) { }

        // which globe last took the sun from the clock, so only it hands it back
        private static CelestialMaster _owner;
#endif

        // angular spring stiffness (1/s^2) and damping ratio for turning towards the light while sleeping; soft
        // and overdamped, so it creeps into place rather than bouncing
        private const float Stiffness = 8f;
        private const float DampingRatio = 1.2f;

        // heavy angular drag while sleeping, so a knock dies off quickly and reads as "grip it to turn it"
        private const float SleepingAngularDrag = 6f;

        // cap (rad/s^2) so a big swing doesn't spin it up violently
        private const float MaxAcceleration = 40f;

        // close enough (degrees) and still enough (rad/s) to stop pushing, so the body can sleep
        private const float RestAngle = 0.5f;
        private const float RestSpeed = 0.02f;

        private Rigidbody _body;
        private Light _sun;
        private bool _active;
        private RigidbodyConstraints _constraints;
        private float _angularDrag;

        /// <summary>
        /// whether the globe is currently driving the sun.
        /// </summary>
        public bool IsActive => _active;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();

            if (_body != null)
            {
                _constraints = _body.constraints;
                _angularDrag = _body.angularDrag;
            }
        }

        private void OnEnable()
        {
            _active = false;

            // spawned free so the floater can lift it; pooled instances may come back pinned or still moving
            if (_body != null)
            {
                _body.isKinematic = false;
                _body.constraints = _constraints;
                _body.angularDrag = SleepingAngularDrag;
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.WakeUp();
            }

            if (!ResolveSun())
                return;

            // snap the globe to wherever the sun currently is
            var rotation = _sun.transform.rotation;
            transform.rotation = rotation;

            if (_body != null)
                _body.rotation = rotation;
        }

        private void OnDisable()
        {
            Sleep();

#if MELONLOADER
            // hand the sun back to the clock
            if (_owner == this)
            {
                _owner = null;

                var driver = StreetyMod.Session?.Sun;
                if (driver != null)
                    driver.Manual = false;
            }
#endif
        }

        /// <summary>
        /// control mode: pin the globe where it is, let it pivot, and drive the sun from its rotation.
        /// </summary>
        public void Activate()
        {
            if (_active || _body == null || !ResolveSun())
                return;

            _active = true;

            // pivot only; the hand turns it, nothing moves it
            _body.isKinematic = false;
            _body.velocity = Vector3.zero;
            _body.constraints = _constraints | RigidbodyConstraints.FreezePosition;
            _body.angularDrag = _angularDrag;
            _body.WakeUp();

#if MELONLOADER
            _owner = this;

            var driver = StreetyMod.Session?.Sun;
            if (driver != null)
                driver.Manual = true;
#endif

            Follow();
        }

        /// <summary>
        /// free the globe again; the sun stays where it was left, and the globe goes back to mirroring it.
        /// </summary>
        public void Sleep()
        {
            if (!_active)
                return;

            _active = false;

            if (_body != null)
            {
                _body.constraints = _constraints;
                _body.angularDrag = SleepingAngularDrag;
                _body.WakeUp();
            }
        }

        /// <summary>
        /// flip between <see cref="Activate"/> and <see cref="Sleep"/>.
        /// </summary>
        public void Toggle()
        {
            if (_active)
                Sleep();
            else
                Activate();
        }

        private void Update()
        {
            if (!_active)
                return;

            if (_body == null || _sun == null)
            {
                Sleep();
                return;
            }

            Follow();
        }

        private void FixedUpdate()
        {
            if (_active || _body == null || _body.isKinematic || _sun == null)
                return;

            // shortest turn from where we're facing to where the light is
            var delta = _sun.transform.rotation * Quaternion.Inverse(_body.rotation);
            delta.ToAngleAxis(out var angle, out var axis);

            if (angle > 180f)
                angle -= 360f;

            var spin = _body.angularVelocity;

            // pushing would keep waking it; let it settle
            if (Mathf.Abs(angle) < RestAngle && spin.magnitude < RestSpeed)
                return;

            // guard the degenerate axis ToAngleAxis hands back for a (near) identity rotation
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 0.5f)
                axis = Vector3.zero;

            // the body's own angular drag already damps it; only make up the difference (never cancel it, or knocks
            // would carry further than the drag says they should)
            var damping = Mathf.Max(0f, 2f * DampingRatio * Mathf.Sqrt(Stiffness) - _body.angularDrag);
            var accel = Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad * Stiffness) - spin * damping, MaxAcceleration);

            _body.AddTorque(accel, ForceMode.Acceleration);
        }

        private void Follow()
        {
            _sun.transform.rotation = transform.rotation;
        }

        private bool ResolveSun()
        {
            if (_sun != null)
                return true;

#if MELONLOADER
            // prefer the light streety is already driving
            _sun = StreetyMod.Session?.Sun?.Light;
#endif

            if (_sun == null)
                _sun = RenderSettings.sun;

            if (_sun == null)
            {
                foreach (var light in FindObjectsOfType<Light>())
                {
                    if (light.type == LightType.Directional)
                    {
                        _sun = light;
                        break;
                    }
                }
            }

            return _sun != null;
        }
    }
}
