using OggVorbisEncoder.Setup.Templates.Psyche;
using OggVorbisEncoder.Setup.Templates.Residue;

namespace OggVorbisEncoder.Setup.Templates;

/// <summary>
/// Represents a stereo 44 setup data template.
/// </summary>
public class Stereo44SetupDataTemplate : ISetupTemplate
{
    /// <summary>
    /// Gets the mappings.
    /// </summary>
public int Mappings => SampleRateMapping.Length - 1;
    /// <summary>
    /// Gets the sample rate mapping.
    /// </summary>
public double[] SampleRateMapping { get; } = Psy44.RateMap_Coupled;
    /// <summary>
    /// Gets the quality mapping.
    /// </summary>
public double[] QualityMapping { get; } = Psy44.QualityMapping;
    /// <summary>
    /// Gets the coupling restriction.
    /// </summary>
public int CouplingRestriction { get; } = 2;
    /// <summary>
    /// Gets the sample rate min restriction.
    /// </summary>
public int SampleRateMinRestriction { get; } = 40000;
    /// <summary>
    /// Gets the sample rate max restriction.
    /// </summary>
public int SampleRateMaxRestriction { get; } = 50000;

    /// <summary>
    /// Gets the block size short.
    /// </summary>
public int[] BlockSizeShort { get; } = Psy44.BlockSizeShort;
    /// <summary>
    /// Gets the block size long.
    /// </summary>
public int[] BlockSizeLong { get; } = Psy44.BlockSizeLong;

    /// <summary>
    /// Gets the psy tone master att.
    /// </summary>
public Att3[] PsyToneMasterAtt { get; } = Psy44.ToneMasterAtt;
    /// <summary>
    /// Gets the psy tone 0 decibel.
    /// </summary>
public int[] PsyTone0Decibel { get; } = Psy.ToneZeroDecibel;
    /// <summary>
    /// Gets the psy tone decibel suppress.
    /// </summary>
public int[] PsyToneDecibelSuppress { get; } = Psy.ToneSuppress;

    /// <summary>
    /// Gets the psy tone adj impulse.
    /// </summary>
public AdjBlock[] PsyToneAdjImpulse { get; } = Psy44.VpToneMaskAdjOtherBlock;
    /// <summary>
    /// Gets the psy tone adj long.
    /// </summary>
public AdjBlock[] PsyToneAdjLong { get; } = Psy44.VpToneMaskAdjLongBlock;
    /// <summary>
    /// Gets the psy tone adj other.
    /// </summary>
public AdjBlock[] PsyToneAdjOther { get; } = Psy44.VpToneMaskAdjOtherBlock;

    /// <summary>
    /// Gets the psy noise guards.
    /// </summary>
public NoiseGuard[] PsyNoiseGuards { get; } = Psy44.NoiseGuards;
    /// <summary>
    /// Gets the psy noise bias impulse.
    /// </summary>
public Noise3[] PsyNoiseBiasImpulse { get; } = Psy.NoiseBiasImpulse;
    /// <summary>
    /// Gets the psy noise bias padding.
    /// </summary>
public Noise3[] PsyNoiseBiasPadding { get; } = Psy.NoiseBiasPadding;
    /// <summary>
    /// Gets the psy noise bias trans.
    /// </summary>
public Noise3[] PsyNoiseBiasTrans { get; } = Psy.NoiseBiasTransition;
    /// <summary>
    /// Gets the psy noise bias long.
    /// </summary>
public Noise3[] PsyNoiseBiasLong { get; } = Psy.NoiseBiasLongBlock;
    /// <summary>
    /// Gets the psy noise decibel suppress.
    /// </summary>
public int[] PsyNoiseDecibelSuppress { get; } = Psy.NoiseSuppress;

    /// <summary>
    /// Gets the psy noise compand.
    /// </summary>
public CompandBlock[] PsyNoiseCompand { get; } = Psy44.Compand;
    /// <summary>
    /// Gets the psy noise compand short mapping.
    /// </summary>
public double[] PsyNoiseCompandShortMapping { get; } = Psy.CompandShortMapping;
    /// <summary>
    /// Gets the psy noise compand long mapping.
    /// </summary>
public double[] PsyNoiseCompandLongMapping { get; } = Psy.CompandLongMapping;

    /// <summary>
    /// Gets the psy noise normal start.
    /// </summary>
public int[][] PsyNoiseNormalStart { get; } = { Psy44.NoiseStartShort, Psy44.NoiseStartLong };
    /// <summary>
    /// Gets the psy noise normal partition.
    /// </summary>
public int[][] PsyNoiseNormalPartition { get; } = { Psy44.NoisePartShort, Psy44.NoisePartLong };
    /// <summary>
    /// Gets the psy noise normal threshold.
    /// </summary>
public double[] PsyNoiseNormalThreshold { get; } = Psy44.NoiseThresh;

    /// <summary>
    /// Gets the psy ath float.
    /// </summary>
public int[] PsyAthFloat { get; } = Psy.AthFloater;
    /// <summary>
    /// Gets the psy ath abs.
    /// </summary>
public int[] PsyAthAbs { get; } = Psy.AthAbs;

    /// <summary>
    /// Gets the psy low pass.
    /// </summary>
public double[] PsyLowPass { get; } = Psy44.Lowpass;

    /// <summary>
    /// Gets the global params.
    /// </summary>
public PsyGlobal[] GlobalParams { get; } = Psy44.Global;
    /// <summary>
    /// Gets the global mapping.
    /// </summary>
public double[] GlobalMapping { get; } = Psy44.GlobalMapping;
    /// <summary>
    /// Gets the stereo modes.
    /// </summary>
public AdjStereo[] StereoModes { get; } = Psy44.StereoModes;

    /// <summary>
    /// Gets the floor books.
    /// </summary>
public IStaticCodeBook[][] FloorBooks { get; } = SharedFloors.FloorBooks;
    /// <summary>
    /// Gets the floor params.
    /// </summary>
public Floor[] FloorParams { get; } = SharedFloors.Floor;
    /// <summary>
    /// Gets the floor mappings.
    /// </summary>
public int[][] FloorMappings { get; } = Psy44.FloorMapping;

    /// <summary>
    /// Gets the maps.
    /// </summary>
public IMappingTemplate[] Maps { get; } = Residue44.MapRes_Coupled;
}
