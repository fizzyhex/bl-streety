using BoneLib;

using UnityEngine;
using UnityEngine.Rendering;

namespace Streety.Render;

/// <summary>
/// a small realtime reflection probe riding on the local player's head, so shiny things pick up whatever city is
/// streamed in around them. the level can't bake one: the world doesn't exist until runtime.
/// </summary>
/// <remarks>
/// the probe is parented to the physics rig's head and re-attached if the rig is rebuilt. it skips the player and ui
/// layers, otherwise it'd capture the inside of the avatar's own head.
/// </remarks>
public sealed class RigReflectionProbe : IDisposable
{
    private const int Resolution = 64;

    // big enough that every renderer in the level falls inside it and uses it
    private const float Extent = 100000f;

    private static readonly string[] ExcludedLayers = { "Player", "UI" };

    private GameObject _probeObject;
    private Transform _head;

    /// <summary>
    /// keeps the probe on the local rig's head; call every frame.
    /// </summary>
    public void Update()
    {
        var head = Player.RigManager?.physicsRig?.m_head;

        if (head == null || (_probeObject != null && _head == head))
            return;

        if (_probeObject == null)
            Create();

        _head = head;
        _probeObject.transform.SetParent(head, false);
        _probeObject.transform.localPosition = Vector3.zero;
    }

    private void Create()
    {
        _probeObject = new GameObject("streety reflection probe");

        var probe = _probeObject.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.EveryFrame;

        // one face per frame: a full refresh every few frames for a sixth of the cost
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;

        probe.resolution = Resolution;
        probe.hdr = true;
        probe.boxProjection = false;
        probe.size = Vector3.one * Extent;
        probe.importance = 100;
        probe.nearClipPlane = 0.3f;
        probe.farClipPlane = Prefs.FarClip.Value;
        probe.cullingMask = BuildCullingMask();
    }

    private static int BuildCullingMask()
    {
        var mask = ~0;

        foreach (var name in ExcludedLayers)
        {
            var layer = LayerMask.NameToLayer(name);

            if (layer >= 0)
                mask &= ~(1 << layer);
        }

        return mask;
    }

    /// <summary>
    /// the probe hangs off the rig, which can outlive the level, so it's always torn down here.
    /// </summary>
    public void Dispose()
    {
        if (_probeObject != null)
            UnityEngine.Object.Destroy(_probeObject);

        _probeObject = null;
        _head = null;
    }
}
