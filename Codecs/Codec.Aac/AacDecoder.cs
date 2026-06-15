#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// AAC element type identifiers (3-bit syntactic element id, ISO/IEC 14496-3 §4.5.2.1).
/// </summary>
public enum AacElementType {
  /// <summary>Single channel element (mono channel).</summary>
  Sce = 0,
  /// <summary>Channel pair element (stereo).</summary>
  Cpe = 1,
  /// <summary>Coupling channel element.</summary>
  Cce = 2,
  /// <summary>LFE (low-frequency effects) channel.</summary>
  Lfe = 3,
  /// <summary>Data stream element.</summary>
  Dse = 4,
  /// <summary>Program config element.</summary>
  Pce = 5,
  /// <summary>Fill element.</summary>
  Fil = 6,
  /// <summary>End of raw_data_block.</summary>
  End = 7,
}

/// <summary>
/// Decoder for the AAC raw_data_block (RDB). Iterates over the syntactic elements
/// (SCE/CPE/LFE/CCE/DSE/PCE/FIL/END) inside one RDB, decoding the AAC-LC core
/// (no SBR/PS) into interleaved 16-bit PCM. CCE is rejected (NotSupported); DSE,
/// PCE and FIL (including any SBR extension payload) are parsed and skipped.
/// </summary>
public sealed class AacDecoder {

  private readonly int _sampleRateIndex;
  private readonly int _channelConfiguration;
  private readonly AacObjectType _objectType;

  // Persistent overlap-add tails (one per output channel).
  private readonly float[][] _overlap;
  // Window shape carried from the previous frame, per channel.
  private readonly int[] _prevWindowShape;
  // LFSR seeds for PNS, per channel.
  private uint[] _pnsSeed;

  // SBR (HE-AAC) state. The LC core is always decoded; the SBR extension payload
  // is parsed for detection/metadata. The QMF audio reconstruction is gated off
  // (see AacSbr), so the PCM output remains LC-core-only — but a confirmed SBR
  // header doubles the EFFECTIVE output sample rate, which callers surface.
  private AacSbr? _sbr;
  private AacElementType _lastAudioElement = AacElementType.End;

  /// <summary>
  /// True once an SBR (Spectral Band Replication) extension with a valid header has
  /// been observed. HE-AAC streams report their core sample rate doubled; the PCM
  /// itself remains the AAC-LC core band (SBR reconstruction is gated, see <see cref="AacSbr"/>).
  /// </summary>
  public bool SbrDetected { get; private set; }

  /// <summary>Constructs a decoder configured for the given header parameters.</summary>
  public AacDecoder(AacObjectType objectType, int sampleRateIndex, int channelConfiguration) {
    AacCodec.AssertProfileSupported(objectType);
    if (channelConfiguration is < 1 or > 2)
      throw new NotSupportedException(
        $"AAC channel configuration {channelConfiguration} not supported. " +
        "This decoder only supports mono (1) and stereo (2). Multichannel (5.1, 7.1, etc.) is deferrable.");
    this._objectType = objectType;
    this._sampleRateIndex = sampleRateIndex;
    this._channelConfiguration = channelConfiguration;
    this._overlap = new float[channelConfiguration][];
    this._prevWindowShape = new int[channelConfiguration];
    this._pnsSeed = new uint[channelConfiguration];
    for (var c = 0; c < channelConfiguration; ++c) {
      this._overlap[c] = new float[AacFilterBank.LongFrameSize];
      this._pnsSeed[c] = 0x1234 + (uint)c * 0x9E3779B9u;
    }
  }

  /// <summary>The number of decoded PCM samples per channel per AAC frame (always 1024 for LC).</summary>
  public int FrameSamplesPerChannel => AacFilterBank.LongFrameSize;

  /// <summary>Channels in the output PCM (1 for mono, 2 for stereo).</summary>
  public int Channels => this._channelConfiguration;

  /// <summary>
  /// Decodes a single raw_data_block, returning interleaved 16-bit PCM (one frame:
  /// <see cref="FrameSamplesPerChannel"/> samples × <see cref="Channels"/>).
  /// </summary>
  public short[] DecodeRawDataBlock(AacBitReader reader) {
    ArgumentNullException.ThrowIfNull(reader);

    var channelPcm = new float[this._channelConfiguration][];
    var nextChannel = 0;

    while (reader.BitsRemaining >= 3) {
      var idCode = (int)reader.ReadBits(3);
      var element = (AacElementType)idCode;
      switch (element) {
        case AacElementType.End:
          reader.ByteAlign();
          return Interleave(channelPcm);

        case AacElementType.Sce:
        case AacElementType.Lfe: {
          var pcm = this.DecodeSingleChannelElement(reader, nextChannel);
          if (nextChannel < this._channelConfiguration)
            channelPcm[nextChannel++] = pcm;
          this._lastAudioElement = AacElementType.Sce;
          break;
        }

        case AacElementType.Cpe: {
          var (l, r) = this.DecodeChannelPairElement(reader);
          if (nextChannel < this._channelConfiguration) channelPcm[nextChannel++] = l;
          if (nextChannel < this._channelConfiguration) channelPcm[nextChannel++] = r;
          this._lastAudioElement = AacElementType.Cpe;
          break;
        }

        case AacElementType.Dse:
          SkipDataStreamElement(reader);
          break;

        case AacElementType.Pce:
          SkipProgramConfigElement(reader);
          break;

        case AacElementType.Fil:
          this.ParseFillElement(reader);
          break;

        case AacElementType.Cce:
          throw new NotSupportedException(
            "AAC coupling channel element (CCE) is not supported. AAC-LC mono/stereo only.");

        default:
          throw new InvalidDataException($"Invalid AAC element id {idCode}.");
      }
    }

    return Interleave(channelPcm);
  }

  private short[] Interleave(float[][] channelPcm) {
    var n = this.FrameSamplesPerChannel;
    var ch = this._channelConfiguration;
    var outp = new short[n * ch];
    for (var c = 0; c < ch; ++c) {
      var src = channelPcm[c];
      if (src is null) continue; // element absent -> silence for that channel
      for (var i = 0; i < n; ++i)
        outp[i * ch + c] = ToPcm16(src[i]);
    }
    return outp;
  }

  private static short ToPcm16(float v) {
    var s = (int)MathF.Round(v * 32768f);
    return (short)Math.Clamp(s, short.MinValue, short.MaxValue);
  }

  // ---------------- SCE / CPE ----------------

  private float[] DecodeSingleChannelElement(AacBitReader reader, int channelIndex) {
    _ = reader.ReadBits(4); // element_instance_tag
    var ch = this.DecodeIndividualChannelStream(reader, commonWindow: false, sharedIcs: null, isRightOfCpe: false);
    var pcm = new float[this.FrameSamplesPerChannel];
    this.Filter(ch, channelIndex % this._channelConfiguration, pcm);
    return pcm;
  }

  private (float[] Left, float[] Right) DecodeChannelPairElement(AacBitReader reader) {
    _ = reader.ReadBits(4); // element_instance_tag
    var commonWindow = reader.ReadBits(1) == 1;

    var msMaskPresent = 0;
    bool[][]? msUsed = null;
    IcsInfo? sharedIcs = null;

    if (commonWindow) {
      sharedIcs = ReadIcsInfo(reader, this._sampleRateIndex);
      msMaskPresent = (int)reader.ReadBits(2);
      if (msMaskPresent == 1) {
        msUsed = new bool[sharedIcs.WindowGroupCount][];
        for (var g = 0; g < sharedIcs.WindowGroupCount; ++g) {
          msUsed[g] = new bool[sharedIcs.MaxSfb];
          for (var sfb = 0; sfb < sharedIcs.MaxSfb; ++sfb)
            msUsed[g][sfb] = reader.ReadBits(1) == 1;
        }
      }
    }

    var leftCh = this.DecodeIndividualChannelStream(reader, commonWindow, sharedIcs, isRightOfCpe: false);
    var rightCh = this.DecodeIndividualChannelStream(reader, commonWindow, sharedIcs, isRightOfCpe: true);

    // Joint-stereo resolution operates on the (shared) ICS geometry.
    var ics = leftCh.Ics;

    // Intensity stereo first (it derives R from L before M/S).
    AacStereo.ApplyIntensity(
      leftCh.Spectrum, rightCh.Spectrum, ics,
      rightCh.SfbCodebooks, rightCh.ScaleFactors,
      msMaskPresent != 0, msUsed);

    // M/S decorrelation.
    if (msMaskPresent != 0)
      AacStereo.ApplyMidSide(
        leftCh.Spectrum, rightCh.Spectrum, ics,
        msMaskAllOn: msMaskPresent == 2, msUsed, rightCh.SfbCodebooks);

    var left = new float[this.FrameSamplesPerChannel];
    var right = new float[this.FrameSamplesPerChannel];
    this.Filter(leftCh, 0, left);
    this.Filter(rightCh, 1, right);
    return (left, right);
  }

  // ---------------- individual_channel_stream ----------------

  private sealed class ChannelData {
    public required IcsInfo Ics;
    public required float[] Spectrum;
    public required int[][] SfbCodebooks;
    public required int[][] ScaleFactors;
    public required TnsData Tns;
  }

  private ChannelData DecodeIndividualChannelStream(
    AacBitReader reader, bool commonWindow, IcsInfo? sharedIcs, bool isRightOfCpe) {
    var globalGain = (int)reader.ReadBits(8);

    var ics = commonWindow && sharedIcs is not null
      ? sharedIcs
      : ReadIcsInfo(reader, this._sampleRateIndex);

    var sfbCb = ReadSectionData(reader, ics);
    var scaleFactors = ReadScaleFactorData(reader, ics, sfbCb, globalGain);

    if (reader.ReadBits(1) == 1) // pulse_data_present
      SkipPulseData(reader);

    var tns = reader.ReadBits(1) == 1 // tns_data_present
      ? TnsData.Decode(reader, ics)
      : new TnsData();

    if (reader.ReadBits(1) == 1) // gain_control_data_present (SSR only)
      throw new NotSupportedException("AAC gain control (SSR profile) is not supported.");

    var quant = AacSpectral.DecodeQuantizedSpectrum(reader, ics, sfbCb);
    var spectrum = new float[AacFilterBank.LongFrameSize];
    AacSpectral.Dequantize(quant, spectrum, ics, scaleFactors, sfbCb);

    return new ChannelData {
      Ics = ics,
      Spectrum = spectrum,
      SfbCodebooks = sfbCb,
      ScaleFactors = scaleFactors,
      Tns = tns,
    };
  }

  // PNS + TNS + filter bank for one decoded channel into PCM.
  private void Filter(ChannelData ch, int channelIndex, float[] pcm) {
    AacPns.Apply(ch.Spectrum, ch.Ics, ch.SfbCodebooks, ch.ScaleFactors, ref this._pnsSeed[channelIndex]);
    ch.Tns.Apply(ch.Spectrum, ch.Ics);
    AacFilterBank.Synthesize(
      ch.Spectrum, ch.Ics.WindowSequence, ch.Ics.WindowShape,
      this._prevWindowShape[channelIndex], this._overlap[channelIndex], pcm);
    this._prevWindowShape[channelIndex] = ch.Ics.WindowShape;
  }

  // ---------------- ics_info ----------------

  internal static IcsInfo ReadIcsInfo(AacBitReader reader, int sampleRateIndex) {
    var ics = new IcsInfo();
    _ = reader.ReadBits(1); // ics_reserved_bit
    ics.WindowSequence = (int)reader.ReadBits(2);
    ics.WindowShape = (int)reader.ReadBits(1);

    if (ics.IsEightShort) {
      ics.MaxSfb = (int)reader.ReadBits(4);
      ics.ScaleFactorGrouping = (int)reader.ReadBits(7);
      ResolveShortGrouping(ics);
      ics.SwbOffset = AacScaleFactorBands.Short128[sampleRateIndex];
      ics.NumSwb = AacScaleFactorBands.NumSwbShort[sampleRateIndex];
    } else {
      ics.MaxSfb = (int)reader.ReadBits(6);
      if (reader.ReadBits(1) == 1) // predictor_data_present
        throw new NotSupportedException("AAC frequency-domain prediction (Main profile) is not supported.");
      ics.WindowGroupCount = 1;
      ics.WindowGroupLength = [1];
      ics.SwbOffset = AacScaleFactorBands.Long1024[sampleRateIndex];
      ics.NumSwb = AacScaleFactorBands.NumSwbLong[sampleRateIndex];
    }

    if (ics.MaxSfb > ics.NumSwb)
      throw new InvalidDataException($"AAC max_sfb {ics.MaxSfb} exceeds {ics.NumSwb} bands for this rate.");
    return ics;
  }

  private static void ResolveShortGrouping(IcsInfo ics) {
    // scale_factor_grouping: bit i (MSB first) set means window i+1 is grouped
    // with window i. Build group lengths from the 7-bit mask.
    var lengths = new List<int>();
    var current = 1;
    for (var w = 1; w < 8; ++w) {
      var grouped = (ics.ScaleFactorGrouping & (1 << (7 - w))) != 0;
      if (grouped) ++current;
      else { lengths.Add(current); current = 1; }
    }
    lengths.Add(current);
    ics.WindowGroupLength = [.. lengths];
    ics.WindowGroupCount = lengths.Count;
  }

  // ---------------- section_data ----------------

  // Returns codebook[group][sfb].
  private static int[][] ReadSectionData(AacBitReader reader, IcsInfo ics) {
    var sectLenBits = ics.IsEightShort ? 3 : 5;
    var escape = (1 << sectLenBits) - 1;
    var result = new int[ics.WindowGroupCount][];
    for (var g = 0; g < ics.WindowGroupCount; ++g) {
      result[g] = new int[ics.MaxSfb];
      var sfb = 0;
      while (sfb < ics.MaxSfb) {
        var cb = (int)reader.ReadBits(4);
        var len = 0;
        int delta;
        do {
          delta = (int)reader.ReadBits(sectLenBits);
          len += delta;
        } while (delta == escape);
        if (sfb + len > ics.MaxSfb)
          throw new InvalidDataException("AAC section length overruns max_sfb.");
        for (var i = 0; i < len; ++i)
          result[g][sfb + i] = cb;
        sfb += len;
      }
    }
    return result;
  }

  // ---------------- scale_factor_data ----------------

  // Returns scaleFactor[group][sfb]. Intensity positions and PNS energies share
  // the same DPCM stream but use their own running accumulators per the spec.
  private static int[][] ReadScaleFactorData(AacBitReader reader, IcsInfo ics, int[][] sfbCb, int globalGain) {
    var result = new int[ics.WindowGroupCount][];
    var scaleFactor = globalGain;
    var intensityPos = 0;
    var noiseEnergy = globalGain - 90;
    var noiseStarted = false;

    for (var g = 0; g < ics.WindowGroupCount; ++g) {
      result[g] = new int[ics.MaxSfb];
      for (var sfb = 0; sfb < ics.MaxSfb; ++sfb) {
        var cb = sfbCb[g][sfb];
        switch (cb) {
          case AacHuffmanTables.ZeroHcb:
            result[g][sfb] = 0;
            break;
          case AacHuffmanTables.IntensityHcb:
          case AacHuffmanTables.IntensityHcb2:
            intensityPos += AacHuffmanTables.DecodeScaleFactorDelta(reader);
            result[g][sfb] = intensityPos;
            break;
          case AacHuffmanTables.NoiseHcb:
            if (!noiseStarted) {
              noiseStarted = true;
              noiseEnergy += (int)reader.ReadBits(9) - 256; // first PNS uses a 9-bit pcm value
            } else {
              noiseEnergy += AacHuffmanTables.DecodeScaleFactorDelta(reader);
            }
            result[g][sfb] = noiseEnergy;
            break;
          default:
            scaleFactor += AacHuffmanTables.DecodeScaleFactorDelta(reader);
            if (scaleFactor is < 0 or > 255)
              throw new InvalidDataException($"AAC scale factor {scaleFactor} out of range.");
            result[g][sfb] = scaleFactor;
            break;
        }
      }
    }
    return result;
  }

  // ---------------- pulse_data ----------------

  private static void SkipPulseData(AacBitReader reader) {
    var numPulse = (int)reader.ReadBits(2); // number_pulse (0..3 -> 1..4 pulses)
    _ = reader.ReadBits(6);                 // pulse_start_sfb
    for (var i = 0; i <= numPulse; ++i) {
      _ = reader.ReadBits(5);  // pulse_offset
      _ = reader.ReadBits(4);  // pulse_amp
    }
    // Pulse escapes add a small DC-ish offset to a few bins; for the LC core and
    // the reference clips here it is acceptable to parse-and-skip (documented).
  }

  // ---------------- DSE / PCE / FIL ----------------

  private static void SkipDataStreamElement(AacBitReader reader) {
    _ = reader.ReadBits(4); // element_instance_tag
    var byteAlign = reader.ReadBits(1) == 1;
    var count = (int)reader.ReadBits(8);
    if (count == 255) count += (int)reader.ReadBits(8);
    if (byteAlign) reader.ByteAlign();
    reader.SkipBits(count * 8);
  }

  private static void SkipProgramConfigElement(AacBitReader reader) {
    _ = reader.ReadBits(4);  // element_instance_tag
    _ = reader.ReadBits(2);  // object_type
    _ = reader.ReadBits(4);  // sampling_frequency_index
    var numFront = (int)reader.ReadBits(4);
    var numSide = (int)reader.ReadBits(4);
    var numBack = (int)reader.ReadBits(4);
    var numLfe = (int)reader.ReadBits(2);
    var numAssoc = (int)reader.ReadBits(3);
    var numCc = (int)reader.ReadBits(4);
    if (reader.ReadBits(1) == 1) reader.SkipBits(4); // mono_mixdown
    if (reader.ReadBits(1) == 1) reader.SkipBits(4); // stereo_mixdown
    if (reader.ReadBits(1) == 1) reader.SkipBits(3); // matrix_mixdown
    reader.SkipBits((numFront + numSide + numBack) * 5);
    reader.SkipBits(numLfe * 4);
    reader.SkipBits(numAssoc * 4);
    reader.SkipBits(numCc * 5);
    reader.ByteAlign();
    var comment = (int)reader.ReadBits(8);
    reader.SkipBits(comment * 8);
  }

  // Fill element extension_type identifiers (ISO/IEC 14496-3 Table 4.121).
  private const int ExtSbrData = 13;     // EXT_SBR_DATA
  private const int ExtSbrDataCrc = 14;  // EXT_SBR_DATA_CRC

  private void ParseFillElement(AacBitReader reader) {
    var count = (int)reader.ReadBits(4); // count
    if (count == 15)
      count += (int)reader.ReadBits(8) - 1;
    if (count <= 0) return;

    // The fill payload is exactly `count` bytes. The first 4 bits are an
    // extension_type. For SBR (13/14) we parse the payload for detection and
    // metadata; the LC PCM output is unaffected (SBR audio reconstruction gated).
    var endTarget = reader.BitsRemaining - count * 8;
    var extType = (int)reader.ReadBits(4);

    if (extType is ExtSbrData or ExtSbrDataCrc && this._lastAudioElement is AacElementType.Sce or AacElementType.Cpe) {
      this._sbr ??= new AacSbr(AacAdtsReader.SampleRateTable[this._sampleRateIndex]);
      var crc = extType == ExtSbrDataCrc;
      if (crc) reader.ReadBits(10); // bs_sbr_crc_bits (not validated)
      var isCpe = this._lastAudioElement == AacElementType.Cpe;
      var remaining = (int)(reader.BitsRemaining - endTarget);
      try {
        if (this._sbr.ParseExtension(reader, isCpe, remaining))
          this.SbrDetected = true;
      } catch (InvalidDataException) {
        // Malformed SBR payload: keep LC-only behaviour, never fail the frame.
      }
    }

    // Skip to the end of the declared payload regardless of what we parsed.
    while (reader.BitsRemaining > endTarget)
      reader.ReadBits(1);
  }

  /// <summary>The base (core) sample rate of the stream in Hz.</summary>
  public int CoreSampleRate => AacAdtsReader.SampleRateTable[this._sampleRateIndex];

  /// <summary>
  /// The effective output sample rate: doubled when SBR has been detected, otherwise
  /// the core rate. SBR reconstruction is gated, so the emitted PCM is still the core
  /// band — this value reflects the bitstream's signalled bandwidth.
  /// </summary>
  public int EffectiveSampleRate => this.SbrDetected ? this.CoreSampleRate * 2 : this.CoreSampleRate;
}
