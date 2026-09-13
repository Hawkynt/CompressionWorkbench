using OggVorbisEncoder.Setup.Templates.Psyche;
using OggVorbisEncoder.Setup.Templates.Residue;

namespace OggVorbisEncoder.Setup.Templates;

/// <summary>
/// Represents an uncoupled xx setup data template.
/// </summary>
public class UncoupledXXSetupDataTemplate : ISetupTemplate
{
    /// <summary>
    /// Gets the mappings.
    /// </summary>
    public int Mappings => SampleRateMapping.Length - 1;
    /// <summary>
    /// Gets the sample rate mapping.
    /// </summary>
    public double[] SampleRateMapping => PsyX.RateMap;
    /// <summary>
    /// Gets the quality mapping.
    /// </summary>
    public double[] QualityMapping => Psy8.QualityMapping;
    /// <summary>
    /// Gets the coupling restriction.
    /// </summary>
    public int CouplingRestriction => -1;
    /// <summary>
    /// Gets the sample rate min restriction.
    /// </summary>
    public int SampleRateMinRestriction => 0;
    /// <summary>
    /// Gets the sample rate max restriction.
    /// </summary>
    public int SampleRateMaxRestriction => 8000;

    /// <summary>
    /// Gets the block size short.
    /// </summary>
    public int[] BlockSizeShort => Psy8.BlockSize;
    /// <summary>
    /// Gets the block size long.
    /// </summary>
    public int[] BlockSizeLong => Psy8.BlockSize;

    /// <summary>
    /// Gets the psy tone master att.
    /// </summary>
    public Att3[] PsyToneMasterAtt => Psy8.ToneMasterAtt;
    /// <summary>
    /// Gets the psy tone 0 decibel.
    /// </summary>
    public int[] PsyTone0Decibel => Psy.ToneZeroDecibel;
    /// <summary>
    /// Gets the psy tone decibel suppress.
    /// </summary>
    public int[] PsyToneDecibelSuppress => Psy.ToneSuppress;

    /// <summary>
    /// Gets the psy tone adj impulse.
    /// </summary>
    public AdjBlock[] PsyToneAdjImpulse => Psy8.VpToneMaskAdjOtherBlock;
    /// <summary>
    /// Gets the psy tone adj long.
    /// </summary>
    public AdjBlock[] PsyToneAdjLong => null;
    /// <summary>
    /// Gets the psy tone adj other.
    /// </summary>
    public AdjBlock[] PsyToneAdjOther => Psy8.VpToneMaskAdjOtherBlock;

    /// <summary>
    /// Gets the psy noise guards.
    /// </summary>
    public NoiseGuard[] PsyNoiseGuards => Psy8.NoiseGuards;
    /// <summary>
    /// Gets the psy noise bias impulse.
    /// </summary>
    public Noise3[] PsyNoiseBiasImpulse => Psy8.NoiseBias;
    /// <summary>
    /// Gets the psy noise bias padding.
    /// </summary>
    public Noise3[] PsyNoiseBiasPadding => Psy8.NoiseBias;
    /// <summary>
    /// Gets the psy noise bias trans.
    /// </summary>
    public Noise3[] PsyNoiseBiasTrans => null;
    /// <summary>
    /// Gets the psy noise bias long.
    /// </summary>
    public Noise3[] PsyNoiseBiasLong => null;
    /// <summary>
    /// Gets the psy noise decibel suppress.
    /// </summary>
    public int[] PsyNoiseDecibelSuppress => Psy.NoiseSuppress;

    /// <summary>
    /// Gets the psy noise compand.
    /// </summary>
    public CompandBlock[] PsyNoiseCompand => Psy8.Compand;
    /// <summary>
    /// Gets the psy noise compand short mapping.
    /// </summary>
    public double[] PsyNoiseCompandShortMapping => Psy8.CompandMapping;
    /// <summary>
    /// Gets the psy noise compand long mapping.
    /// </summary>
    public double[] PsyNoiseCompandLongMapping => null;

    /// <summary>
    /// Gets the psy noise normal start.
    /// </summary>
    public int[][] PsyNoiseNormalStart => new int[][] { Psy8.NoiseStart, Psy8.NoiseStart };
    /// <summary>
    /// Gets the psy noise normal partition.
    /// </summary>
    public int[][] PsyNoiseNormalPartition => new int[][] { Psy8.NoisePart, Psy8.NoisePart };
    /// <summary>
    /// Gets the psy noise normal threshold.
    /// </summary>
    public double[] PsyNoiseNormalThreshold => Psy44.NoiseThresh5Only;

    /// <summary>
    /// Gets the psy ath float.
    /// </summary>
    public int[] PsyAthFloat => Psy8.AthFloater;
    /// <summary>
    /// Gets the psy ath abs.
    /// </summary>
    public int[] PsyAthAbs => Psy8.AthAbs;

    /// <summary>
    /// Gets the psy low pass.
    /// </summary>
    public double[] PsyLowPass => Psy8.Lowpass;

    /// <summary>
    /// Gets the global params.
    /// </summary>
    public PsyGlobal[] GlobalParams => Psy44.Global;
    /// <summary>
    /// Gets the global mapping.
    /// </summary>
    public double[] GlobalMapping => Psy8.GlobalMapping;
    /// <summary>
    /// Gets the stereo modes.
    /// </summary>
    public AdjStereo[] StereoModes => Psy8.StereoModes;

    /// <summary>
    /// Gets the floor books.
    /// </summary>
    public IStaticCodeBook[][] FloorBooks => SharedFloors.FloorBooks;
    /// <summary>
    /// Gets the floor params.
    /// </summary>
    public Floor[] FloorParams => SharedFloors.Floor;
    /// <summary>
    /// Gets the floor mappings.
    /// </summary>
    public int[][] FloorMappings => Psy8.FloorMapping;

    /// <summary>
    /// Gets the maps.
    /// </summary>
    public IMappingTemplate[] Maps => Residue44.MapRes_Uncoupled;
}
