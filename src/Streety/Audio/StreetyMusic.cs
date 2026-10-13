using BoneLib;

using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Audio;
using Il2CppSLZ.Marrow.Warehouse;

using UnityEngine;

namespace Streety.Audio;

/// <summary>
/// layered terrain music, loaded from the streety pallet's monodiscs.
/// </summary>
/// <remarks>
/// the base track loops while an area is loaded. two flying stems loop alongside it, sample-locked, and fade in with movement:
/// 1x for hovering or idling in a vehicle (settles back to base after a while), 2x for flying or throttling.
/// routed through bonelab's non-diegetic music mixer, so the game's music volume applies.
/// </remarks>
public sealed class StreetyMusic : IDisposable
{
    public enum Intensity
    {
        None,
        Idle,
        Active,
    }

    private static readonly MonoDiscReference TerrainReference = new("Fizzyhex.streety.MonoDisc.GEmusicterrain");
    private static readonly MonoDiscReference Flying1xReference = new("Fizzyhex.streety.MonoDisc.gemusicterrainflying");
    private static readonly MonoDiscReference Flying2xReference = new("Fizzyhex.streety.MonoDisc.gemusicterrainflying2x");

    private const float BaseFadeSeconds = 2f;
    private const float LayerFadeSeconds = 1.25f;

    // dropping a layer waits this long, so a bump off the ground or a trigger flutter doesn't chop the music
    private const float ReleaseSeconds = 0.75f;

    // hovering or sitting still this long settles back to the base track
    private const float IdleTimeoutSeconds = 10f;

    // stems further apart than this get snapped back onto the base
    private const int DriftSamples = 1024;

    private readonly GameObject _object;
    private readonly AudioSource _base;
    private readonly AudioSource _flying1x;
    private readonly AudioSource _flying2x;

    private int _pendingLoads;
    private bool _ready;
    private bool _paused;
    private Intensity _intensity;
    private float _releaseAt;
    private float _idleSince;

    public StreetyMusic()
    {
        _object = new GameObject("streety music");

        _base = CreateSource();
        _flying1x = CreateSource();
        _flying2x = CreateSource();

        Load(TerrainReference, _base);
        Load(Flying1xReference, _flying1x);
        Load(Flying2xReference, _flying2x);
    }

    private AudioSource CreateSource()
    {
        var source = _object.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f;
        source.volume = 0f;
        source.priority = 0;
        source.bypassReverbZones = true;
        return source;
    }

    private void Load(MonoDiscReference reference, AudioSource source)
    {
        var disc = reference.DataCard;

        if (disc == null)
        {
            StreetyMod.Log.Warning($"missing monodisc {reference.Barcode.ID}; is the streety pallet installed?");
            return;
        }

        _pendingLoads++;

        disc.AudioClip.LoadAsset((Action<AudioClip>)(clip =>
        {
            _pendingLoads--;

            // the session may have ended while the clip was loading
            if (_object == null)
                return;

            if (clip == null)
                StreetyMod.Log.Warning($"couldn't load {reference.Barcode.ID}");
            else
                source.clip = clip;
        }));
    }

    /// <summary>
    /// fades the music towards its target mix; call every frame.
    /// </summary>
    /// <param name="playing">false while there's no area loaded, e.g. waiting on the warp platform.</param>
    public void Update(bool playing)
    {
        if (_object == null)
            return;

        if (!_ready)
        {
            if (!playing || _pendingLoads > 0 || _base.clip == null)
                return;

            Start();
        }

        var measured = playing ? Measure() : Intensity.None;

        if (measured != Intensity.Idle)
            _idleSince = Time.unscaledTime;
        else if (Time.unscaledTime - _idleSince > IdleTimeoutSeconds)
            measured = Intensity.None;

        UpdateIntensity(measured);

        var fadeBase = Time.unscaledDeltaTime / BaseFadeSeconds;
        var fadeLayer = Time.unscaledDeltaTime / LayerFadeSeconds;
        var volume = Prefs.MusicVolume.Value;

        Fade(_base, playing ? volume : 0f, fadeBase * volume);
        Fade(_flying1x, _intensity == Intensity.Idle ? volume : 0f, fadeLayer * volume);
        Fade(_flying2x, _intensity == Intensity.Active ? volume : 0f, fadeLayer * volume);

        // fully faded out: pause rather than burn through the track while nobody can hear it
        var silent = !playing && _base.volume <= 0f && _flying1x.volume <= 0f && _flying2x.volume <= 0f;

        if (silent != _paused)
            SetPaused(silent);

        if (!_paused)
        {
            Sync(_flying1x);
            Sync(_flying2x);
        }
    }

    private void Start()
    {
        _ready = true;

        var mixer = Audio3dManager.nonDiegeticMusic;

        if (mixer == null)
            StreetyMod.Log.Warning("no music mixer yet; music won't follow the game's music volume");

        // schedule every stem on the same dsp tick so they start sample-locked
        var start = AudioSettings.dspTime + 0.1;

        foreach (var source in Sources())
        {
            source.outputAudioMixerGroup = mixer;
            source.PlayScheduled(start);
        }

        _paused = false;
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;

        foreach (var source in Sources())
        {
            if (paused)
                source.Pause();
            else
                source.UnPause();
        }
    }

    private IEnumerable<AudioSource> Sources()
    {
        yield return _base;

        if (_flying1x.clip != null)
            yield return _flying1x;

        if (_flying2x.clip != null)
            yield return _flying2x;
    }

    private void Sync(AudioSource layer)
    {
        if (layer.clip == null || !layer.isPlaying || !_base.isPlaying)
            return;

        var target = _base.timeSamples % layer.clip.samples;

        if (Math.Abs(layer.timeSamples - target) > DriftSamples)
            layer.timeSamples = target;
    }

    private static void Fade(AudioSource source, float target, float step)
    {
        if (source.clip == null)
            return;

        source.volume = Mathf.MoveTowards(source.volume, target, Mathf.Max(step, 0.0001f));
    }

    private void UpdateIntensity(Intensity measured)
    {
        if (measured >= _intensity)
        {
            _intensity = measured;
            _releaseAt = Time.unscaledTime + ReleaseSeconds;
            return;
        }

        if (Time.unscaledTime >= _releaseAt)
            _intensity = measured;
    }

    /// <summary>
    /// how much the local player is moving: idle in the air or a vehicle, or flying / throttling.
    /// </summary>
    private static Intensity Measure()
    {
        var rig = Player.RigManager;

        if (rig == null)
            return Intensity.None;

        var seat = rig.activeSeat;

        if (seat != null)
            return IsThrottling(seat) ? Intensity.Active : Intensity.Idle;

        var physicsRig = rig.physicsRig;

        if (physicsRig == null)
            return Intensity.None;

        var flying = false;
        var holding = false;

        foreach (var hand in new[] { physicsRig.leftHand, physicsRig.rightHand })
        {
            var held = hand?.m_CurrentAttachedGO;

            if (held == null || held.GetComponentInParent<FlyingGun>() == null)
                continue;

            holding = true;

            if (hand.Controller != null && hand.Controller.GetPrimaryInteractionButtonAxis() > 0.1f)
                flying = true;
        }

        if (flying)
            return Intensity.Active;

        if (!holding)
            return Intensity.None;

        var grounded = physicsRig.physG == null || physicsRig.physG.isGrounded;
        return grounded ? Intensity.None : Intensity.Idle;
    }

    private static bool IsThrottling(Seat seat)
    {
        // the gokart drives through an atv
        var atv = seat.GetComponentInParent<Atv>();

        if (atv != null)
            return Mathf.Abs(atv._lastThrottleSet) > 0.05f;

        // anything else: going by whether the driver's pushing the stick
        return seat._primaryAxis.sqrMagnitude > 0.04f;
    }

    public void Dispose()
    {
        if (_object != null)
            UnityEngine.Object.Destroy(_object);
    }
}
