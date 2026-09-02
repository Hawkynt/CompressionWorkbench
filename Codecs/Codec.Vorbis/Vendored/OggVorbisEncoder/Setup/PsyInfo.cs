using System.Linq;

namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a psy info.
/// </summary>
public class PsyInfo
{
        /// <summary>
    /// Defines the bands constant value.
    /// </summary>
public const int Bands = 17;
    private const int NoiseCompandLevels = 40;
    private const int NoiseCurves = 3;
    private float[] _noiseCompand;
    private float[][] _noiseOffset;
    private float[] _toneAtt;

    private float[] _toneMasterAtt;

        /// <summary>
    /// Initializes a new instance of <see cref="PsyInfo"/>.
    /// </summary>
public PsyInfo(
        int blockFlag,
        float athAdjAtt,
        float athMaxAtt,
        float[] toneMasterAtt,
        float toneCenterBoost,
        float toneDecay,
        float toneAbsLimit,
        float[] toneAtt,
        int noiseMaskP,
        float noiseMaxSuppress,
        float noiseWindowLow,
        float noiseWindowHigh,
        int noiseWindowLowMin,
        int noiseWindowHighMin,
        int noiseWindowFixed,
        float[][] noiseOffset,
        float[] noiseCompand,
        float maxCurveDecibel,
        bool normalize,
        int normalStart,
        int normalPartition,
        double normalThreshold)
    {
        BlockFlag = blockFlag;
        AthAdjAtt = athAdjAtt;
        AthMaxAtt = athMaxAtt;
        ToneMasterAtt = toneMasterAtt;
        ToneCenterBoost = toneCenterBoost;
        ToneDecay = toneDecay;
        ToneAbsLimit = toneAbsLimit;
        ToneAtt = toneAtt;
        NoiseMaskP = noiseMaskP;
        NoiseMaxSuppress = noiseMaxSuppress;
        NoiseWindowLow = noiseWindowLow;
        NoiseWindowHigh = noiseWindowHigh;
        NoiseWindowLowMin = noiseWindowLowMin;
        NoiseWindowHighMin = noiseWindowHighMin;
        NoiseWindowFixed = noiseWindowFixed;
        NoiseOffset = noiseOffset;
        NoiseCompand = noiseCompand;
        MaxCurveDecibel = maxCurveDecibel;
        Normalize = normalize;
        NormalStart = normalStart;
        NormalPartition = normalPartition;
        NormalThreshold = normalThreshold;
    }

        /// <summary>
    /// Gets or sets the block flag.
    /// </summary>
public int BlockFlag { get; set; }

        /// <summary>
    /// Gets or sets the ath adj att.
    /// </summary>
public float AthAdjAtt { get; set; }
        /// <summary>
    /// Gets or sets the ath max att.
    /// </summary>
public float AthMaxAtt { get; set; }

        /// <summary>
    /// Gets or sets the tone master att.
    /// </summary>
public float[] ToneMasterAtt
    {
        get { return _toneMasterAtt; }
        private set { _toneMasterAtt = value.ToFixedLength(NoiseCurves); }
    }

        /// <summary>
    /// Gets or sets the tone center boost.
    /// </summary>
public float ToneCenterBoost { get; set; }
        /// <summary>
    /// Gets or sets the tone decay.
    /// </summary>
public float ToneDecay { get; set; }
        /// <summary>
    /// Gets or sets the tone abs limit.
    /// </summary>
public float ToneAbsLimit { get; set; }

        /// <summary>
    /// Gets or sets the tone att.
    /// </summary>
public float[] ToneAtt
    {
        get { return _toneAtt; }
        private set { _toneAtt = value.ToFixedLength(Bands); }
    }

        /// <summary>
    /// Gets the noise mask p.
    /// </summary>
public int NoiseMaskP { get; }
        /// <summary>
    /// Gets or sets the noise max suppress.
    /// </summary>
public float NoiseMaxSuppress { get; set; }
        /// <summary>
    /// Gets the noise window low.
    /// </summary>
public float NoiseWindowLow { get; }
        /// <summary>
    /// Gets the noise window high.
    /// </summary>
public float NoiseWindowHigh { get; }
        /// <summary>
    /// Gets or sets the noise window low min.
    /// </summary>
public int NoiseWindowLowMin { get; set; }
        /// <summary>
    /// Gets or sets the noise window high min.
    /// </summary>
public int NoiseWindowHighMin { get; set; }
        /// <summary>
    /// Gets or sets the noise window fixed.
    /// </summary>
public int NoiseWindowFixed { get; set; }

        /// <summary>
    /// Gets or sets the noise offset.
    /// </summary>
public float[][] NoiseOffset
    {
        get { return _noiseOffset; }
        private set
        {
            var fixedValue = value.Select(s => s.ToFixedLength(Bands).ToArray());
            _noiseOffset = fixedValue.ToArray().ToFixedLength(NoiseCurves);
        }
    }

        /// <summary>
    /// Gets or sets the noise compand.
    /// </summary>
public float[] NoiseCompand
    {
        get { return _noiseCompand; }
        private set { _noiseCompand = value.ToFixedLength(NoiseCompandLevels); }
    }

        /// <summary>
    /// Gets or sets the max curve decibel.
    /// </summary>
public float MaxCurveDecibel { get; set; }

        /// <summary>
    /// Gets a value indicating whether normalize.
    /// </summary>
public bool Normalize { get; set; }
        /// <summary>
    /// Gets or sets the normal start.
    /// </summary>
public int NormalStart { get; set; }
        /// <summary>
    /// Gets or sets the normal partition.
    /// </summary>
public int NormalPartition { get; set; }
        /// <summary>
    /// Gets or sets the normal threshold.
    /// </summary>
public double NormalThreshold { get; set; }

        /// <summary>
    /// Performs the clone operation.
    /// </summary>
public PsyInfo Clone() => new PsyInfo(
        BlockFlag,
        AthAdjAtt,
        AthMaxAtt,
        ToneMasterAtt.ToArray(),
        ToneCenterBoost,
        ToneDecay,
        ToneAbsLimit,
        ToneAtt.ToArray(),
        NoiseMaskP,
        NoiseMaxSuppress,
        NoiseWindowLow,
        NoiseWindowHigh,
        NoiseWindowLowMin,
        NoiseWindowHighMin,
        NoiseWindowFixed,
        NoiseOffset.Select(s => s.ToArray()).ToArray(),
        NoiseCompand.ToArray(),
        MaxCurveDecibel,
        Normalize,
        NormalStart,
        NormalPartition,
        NormalThreshold);
}
