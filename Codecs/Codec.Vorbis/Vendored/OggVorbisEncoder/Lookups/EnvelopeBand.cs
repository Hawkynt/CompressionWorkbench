using System;

namespace OggVorbisEncoder.Lookups;

/// <summary>
/// Represents an envelope band.
/// </summary>
public class EnvelopeBand
{
    /// <summary>
    /// Initializes a new instance of <see cref="EnvelopeBand"/>.
    /// </summary>
    public EnvelopeBand(
        int begin,
        int windowLength)
    {
        Begin = begin;
        Window = new float[windowLength];

        for (var i = 0; i < Window.Length; i++)
        {
            Window[i] = (float)Math.Sin((i + .5) / Window.Length * Math.PI);
            Total += Window[i];
        }

        Total = (float)(1.0 / Total);
    }

    /// <summary>
    /// Gets the begin.
    /// </summary>
    public int Begin { get; }
    /// <summary>
    /// Gets the window.
    /// </summary>
    public float[] Window { get; }
    /// <summary>
    /// Gets the total.
    /// </summary>
    public float Total { get; }
}
