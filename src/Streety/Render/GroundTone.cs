using UnityEngine;

namespace Streety.Render;

/// <summary>
/// the average colour of the ground around the current location, read from tile imagery as it streams in.
/// </summary>
/// <remarks>
/// coarse tiles arrive first after a warp and each covers a wide area, so the first few samples describe the region
/// rather than the street. <see cref="Climate"/> reads greenness, aridity and snow off it.
/// </remarks>
public sealed class GroundTone
{
    private const int MaxSamples = 32;
    private const int ConfidentSamples = 12;
    private const float SampleInterval = 0.2f;
    private const int Grid = 8;

    private Color _sum;
    private int _samples;
    private float _nextSample;

    public float Confidence => Mathf.Clamp01(_samples / (float)ConfidentSamples);

    /// <summary>
    /// mean imagery colour (srgb).
    /// </summary>
    public Color Mean => _samples > 0 ? _sum / _samples : Color.grey;

    /// <summary>
    /// how much greener than grey the ground is; vegetation.
    /// </summary>
    public float Greenness
    {
        get
        {
            var c = Mean;
            return Mathf.Clamp01((c.g - (c.r + c.b) * 0.5f) / Mathf.Max(c.maxColorComponent, 0.05f) * 4f);
        }
    }

    /// <summary>
    /// sand and bare earth: warm, unsaturated green.
    /// </summary>
    public float Aridity
    {
        get
        {
            var c = Mean;
            var warmth = (c.r - c.b) / Mathf.Max(c.maxColorComponent, 0.05f);
            return Mathf.Clamp01((warmth - 0.12f) * 4f) * (1f - Mathf.Clamp01(Greenness * 2f));
        }
    }

    public float Snow
    {
        get
        {
            Color.RGBToHSV(Mean, out _, out var saturation, out var value);
            return Mathf.Clamp01((value - 0.65f) * 5f) * Mathf.Clamp01(1f - saturation * 6f);
        }
    }

    /// <summary>
    /// whether the next decoded image should be sampled; keeps it to a handful per second.
    /// </summary>
    public bool Wants => _samples < MaxSamples && Time.unscaledTime >= _nextSample;

    /// <summary>
    /// averages a readable texture on a coarse grid.
    /// </summary>
    public void Sample(Texture2D texture)
    {
        _nextSample = Time.unscaledTime + SampleInterval;

        var sum = Color.clear;

        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
            sum += texture.GetPixelBilinear((x + 0.5f) / Grid, (y + 0.5f) / Grid);

        _sum += sum / (Grid * Grid);
        _samples++;
    }

    /// <summary>
    /// forget the old place after a warp.
    /// </summary>
    public void Reset()
    {
        _sum = Color.clear;
        _samples = 0;
    }
}
