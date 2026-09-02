using System.Linq;

namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a psy global.
/// </summary>
public class PsyGlobal
{
    /// <summary>
    /// Defines the envelope bands constant value.
    /// </summary>
public const int EnvelopeBands = 7;
    /// <summary>
    /// Defines the packet blobs constant value.
    /// </summary>
public const int PacketBlobs = 15;

    /// <summary>
    /// Initializes a new instance of <see cref="PsyGlobal"/>.
    /// </summary>
public PsyGlobal(
        int eighthOctaveLines,
        float[] preEchoThreshold,
        float[] postEchoThreshold,
        float stretchPenalty,
        float preEchoMinEnergy,
        float ampMaxAttPerSecond,
        int[] couplingPerKilohertz,
        int[][] couplingPointLimit,
        int[] couplingPrePointAmp,
        int[] couplingPostPointAmp,
        int[][] slidingLowPass)
    {
        EighthOctaveLines = eighthOctaveLines;

        PreEchoThreshold = preEchoThreshold.ToFixedLength(EnvelopeBands);
        PostEchoThreshold = postEchoThreshold.ToFixedLength(EnvelopeBands);

        StretchPenalty = stretchPenalty;
        PreEchoMinEnergy = preEchoMinEnergy;
        AmpMaxAttPerSec = ampMaxAttPerSecond;

        CouplingPerKilohertz = couplingPerKilohertz.ToFixedLength(PacketBlobs);
        CouplingPrePointAmp = couplingPrePointAmp.ToFixedLength(PacketBlobs);
        CouplingPostPointAmp = couplingPostPointAmp.ToFixedLength(PacketBlobs);

        CouplingPointLimit = couplingPointLimit.Select(s => s.ToFixedLength(PacketBlobs)).ToArray();
        SlidingLowPass = slidingLowPass.Select(s => s.ToFixedLength(PacketBlobs)).ToArray();
    }

    /// <summary>
    /// Gets the eighth octave lines.
    /// </summary>
public int EighthOctaveLines { get; }

    // for block long/short tuning; encode only 
    /// <summary>
    /// Gets the pre echo threshold.
    /// </summary>
public float[] PreEchoThreshold { get; }
    /// <summary>
    /// Gets the post echo threshold.
    /// </summary>
public float[] PostEchoThreshold { get; }
    /// <summary>
    /// Gets the stretch penalty.
    /// </summary>
public float StretchPenalty { get; }
    /// <summary>
    /// Gets the pre echo min energy.
    /// </summary>
public float PreEchoMinEnergy { get; }
    /// <summary>
    /// Gets or sets the amp max att per sec.
    /// </summary>
public float AmpMaxAttPerSec { get; set; }

    // channel coupling config 
    /// <summary>
    /// Gets the coupling per kilohertz.
    /// </summary>
public int[] CouplingPerKilohertz { get; }
    /// <summary>
    /// Gets the coupling point limit.
    /// </summary>
public int[][] CouplingPointLimit { get; }
    /// <summary>
    /// Gets or sets the coupling pre point amp.
    /// </summary>
public int[] CouplingPrePointAmp { get; set; }
    /// <summary>
    /// Gets or sets the coupling post point amp.
    /// </summary>
public int[] CouplingPostPointAmp { get; set; }
    /// <summary>
    /// Gets the sliding low pass.
    /// </summary>
public int[][] SlidingLowPass { get; }

    /// <summary>
    /// Performs the clone operation.
    /// </summary>
public PsyGlobal Clone() => new PsyGlobal(
        EighthOctaveLines,
        PreEchoThreshold.ToArray(),
        PostEchoThreshold.ToArray(),
        StretchPenalty,
        PreEchoMinEnergy,
        AmpMaxAttPerSec,
        CouplingPerKilohertz.ToArray(),
        CouplingPointLimit.Select(s => s.ToArray()).ToArray(),
        CouplingPrePointAmp.ToArray(),
        CouplingPostPointAmp.ToArray(),
        SlidingLowPass.Select(s => s.ToArray()).ToArray());
}
