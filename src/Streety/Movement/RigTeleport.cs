using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Utilities;

using UnityEngine;

namespace Streety.Movement;

/// <summary>
/// teleports a rig so held props don't cause flinging.
/// </summary>
/// <remarks>
/// mirrors <c>RigManagerExtensions.TeleportToPosition</c>.
/// the physics rig is snapped back to its default pose which lets bonelab carry whatever they hold.
/// </remarks>
public static class RigTeleport
{
    /// <summary>
    /// moves the rig so its centre of pressure (between the feet) lands on <paramref name="position"/>.
    /// </summary>
    public static void To(RigManager rigManager, Vector3 position, bool resetVelocity = true)
    {
        var physicsRig = rigManager.physicsRig;

        ResetPose(physicsRig, resetVelocity);
        physicsRig.centerOfPressure.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

        Move(physicsRig, position, resetVelocity);

        physicsRig.ResetHands(Handedness.BOTH);

        foreach (var rig in rigManager.remapRigs)
            Move(rig, position, resetVelocity);
    }

    private static void Move(Rig rig, Vector3 position, bool resetVelocity)
    {
        var displace = SimpleTransform.Create(position - rig.centerOfPressure.position, Quaternion.identity);

        rig.Teleport(displace, resetVelocity);
    }

    private static void ResetPose(PhysicsRig physicsRig, bool resetVelocity)
    {
        var entity = physicsRig.marrowEntity;
        var poses = entity._defaultPoseCache;
        var bodies = entity.Bodies;

        for (var i = 0; i < poses.Length; i++)
        {
            var body = bodies[i];

            body.transform.localPosition = poses[i].position;
            body.transform.localRotation = poses[i].rotation;

            if (!resetVelocity)
                continue;

            var rigidbody = body._rigidbody;

            if (rigidbody == null)
                continue;

            rigidbody.velocity = Vector3.zero;
            rigidbody.angularVelocity = Vector3.zero;
        }
    }
}
