using System;
using OggVorbisEncoder.Setup;

namespace OggVorbisEncoder.Lookups;

/// <summary>
/// Represents a psy global lookup.
/// </summary>
public class PsyGlobalLookup
{
    private const int NegativeInfinite = -9999;
    private readonly PsyGlobal _psyGlobal;
    private float _ampMax;

        /// <summary>
    /// Initializes a new instance of <see cref="PsyGlobalLookup"/>.
    /// </summary>
public PsyGlobalLookup(PsyGlobal global)
    {
        _psyGlobal = global;
        AmpMax = NegativeInfinite;
    }

        /// <summary>
    /// Gets or sets the amp max.
    /// </summary>
public float AmpMax
    {
        get { return _ampMax; }
        private set { _ampMax = Math.Max(NegativeInfinite, value); }
    }

        /// <summary>
    /// Performs the decay amp max operation.
    /// </summary>
public void DecayAmpMax(int n, int sampleRate)
    {
        var secs = (float)n / sampleRate;
        AmpMax += secs * _psyGlobal.AmpMaxAttPerSec;
    }
}
