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
/// (SCE/CPE/LFE/CCE/DSE/PCE/FIL/END) inside one RDB, decoding LC-profile audio
/// elements into PCM samples. Currently the element loop is implemented and
/// header parsing is complete; the spectral / IMDCT pipeline is gated by
/// <see cref="NotSupportedException"/> until the spec tables are populated.
/// </summary>
public sealed class AacDecoder {

  private readonly int _sampleRateIndex;
  private readonly int _channelConfiguration;
  private readonly AacObjectType _objectType;

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
  }

  /// <summary>The number of decoded PCM samples per channel per AAC frame (always 1024 for LC).</summary>
  public int FrameSamplesPerChannel => AacFilterBank.LongFrameSize;

  /// <summary>Channels in the output PCM (1 for mono, 2 for stereo).</summary>
  public int Channels => this._channelConfiguration;

  /// <summary>
  /// Decodes a single raw_data_block from the bit reader. Throws
  /// <see cref="NotSupportedException"/> for the (unimplemented) spectral pipeline.
  /// </summary>
  public short[] DecodeRawDataBlock(AacBitReader reader) {
    ArgumentNullException.ThrowIfNull(reader);
    while (reader.BitsRemaining >= 3) {
      var idCode = (int)reader.ReadBits(3);
      var element = (AacElementType)idCode;
      switch (element) {
        case AacElementType.End:
          reader.ByteAlign();
          return new short[this.FrameSamplesPerChannel * this.Channels];
        case AacElementType.Sce:
        case AacElementType.Lfe:
          this.DecodeSingleChannelElement(reader);
          break;
        case AacElementType.Cpe:
          this.DecodeChannelPairElement(reader);
          break;
        case AacElementType.Dse:
        case AacElementType.Fil:
        case AacElementType.Pce:
        case AacElementType.Cce:
          throw new NotSupportedException(
            $"AAC element type {element} parsing not implemented. The dispatcher " +
            "is in place but DSE/FIL/PCE/CCE bodies are deferred along with the " +
            "spectral pipeline.");
        default:
          throw new InvalidDataException($"Invalid AAC element id {idCode}.");
      }
    }
    return [];
  }

  private void DecodeSingleChannelElement(AacBitReader reader) {
    _ = reader.ReadBits(4); // element_instance_tag
    throw new NotSupportedException(
      $"AAC SCE individual_channel_stream not yet implemented for {this._objectType}. " +
      "Requires ICS info parsing (window_sequence, window_shape, max_sfb, scalefactor_grouping), " +
      "section_data + scale_factor_data, pulse_data, TNS, gain_control, and spectral data " +
      "per ISO/IEC 14496-3 §4.5.2.3.");
  }

  private void DecodeChannelPairElement(AacBitReader reader) {
    _ = reader.ReadBits(4); // element_instance_tag
    var commonWindow = reader.ReadBits(1) == 1;
    _ = commonWindow;
    throw new NotSupportedException(
      $"AAC CPE channel_pair_element not yet implemented for {this._objectType} " +
      $"@ rate index {this._sampleRateIndex}. Requires common-window ICS info, " +
      "ms_mask + ms_used, two individual_channel_streams, and inverse M/S decorrelation.");
  }
}
