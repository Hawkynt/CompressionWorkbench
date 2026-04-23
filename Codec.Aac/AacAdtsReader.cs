#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// AAC MPEG Audio Object Types (subset). Values match ISO/IEC 14496-3 Table 1.16.
/// ADTS stores this minus 1 in its 2-bit <c>profile</c> field.
/// </summary>
public enum AacObjectType {
  Null = 0,
  AacMain = 1,
  AacLc = 2,
  AacSsr = 3,
  AacLtp = 4,
  Sbr = 5,         // HE-AAC extension (Spectral Band Replication)
  AacScalable = 6,
  TwinVQ = 7,
  Celp = 8,
  Hvxc = 9,
  Er_AacLc = 17,
  Er_AacLtp = 19,
  Er_AacScalable = 20,
  Ps = 29,          // HE-AAC v2 (Parametric Stereo)
}

/// <summary>
/// Parsed contents of a single 7-byte (CRC-absent) ADTS header + the 2 CRC bytes
/// when <c>ProtectionAbsent</c> is <c>false</c>. <c>Profile</c> is the ADTS field
/// (0..3); add 1 to obtain the MPEG-4 <see cref="AacObjectType"/>.
/// </summary>
public readonly record struct AdtsHeader(
  bool IsMpeg2,
  bool ProtectionAbsent,
  int Profile,
  int SampleRateIndex,
  int SampleRate,
  int ChannelConfiguration,
  int FrameLength,
  int BufferFullness,
  int NumberOfRawDataBlocks,
  int HeaderLengthBytes) {

  /// <summary>The decoded object type (profile+1).</summary>
  public AacObjectType ObjectType => (AacObjectType)(this.Profile + 1);
}

/// <summary>
/// Parser for ADTS (Audio Data Transport Stream) framing as defined by
/// ISO/IEC 13818-7 §5.4.1. ADTS wraps raw AAC frames in a self-synchronising
/// header with a 12-bit sync word (<c>0xFFF</c>), sample-rate index, channel
/// configuration and frame length.
/// </summary>
public static class AacAdtsReader {

  /// <summary>Sample-rate lookup (indices 0..12). Indices 13..14 are reserved, 15 is explicit.</summary>
  public static readonly int[] SampleRateTable = [
    96000, 88200, 64000, 48000, 44100, 32000,
    24000, 22050, 16000, 12000, 11025, 8000, 7350,
    0, 0, 0,
  ];

  /// <summary>Minimum ADTS header length (protection absent=1, no CRC).</summary>
  public const int ShortHeaderLength = 7;

  /// <summary>ADTS header length with CRC (protection absent=0).</summary>
  public const int LongHeaderLength = 9;

  /// <summary>
  /// Parses a 7- or 9-byte ADTS header starting at <paramref name="offset"/> in
  /// <paramref name="buffer"/>. Throws <see cref="InvalidDataException"/> if the
  /// sync word or layer bits don't match.
  /// </summary>
  public static AdtsHeader ParseHeader(ReadOnlySpan<byte> buffer, int offset = 0) {
    if (buffer.Length - offset < ShortHeaderLength)
      throw new InvalidDataException("Buffer too small to contain an ADTS header.");

    // syncword = 0xFFF (12 bits)
    if (buffer[offset] != 0xFF || (buffer[offset + 1] & 0xF0) != 0xF0)
      throw new InvalidDataException("ADTS sync word missing (expected 0xFFF).");

    var b1 = buffer[offset + 1];
    var isMpeg2 = (b1 & 0x08) != 0;          // ID flag (1 bit)
    var layer = (b1 >> 1) & 0x03;            // must be 0
    if (layer != 0)
      throw new InvalidDataException($"ADTS layer must be 0, got {layer}.");
    var protectionAbsent = (b1 & 0x01) != 0;

    var b2 = buffer[offset + 2];
    var profile = (b2 >> 6) & 0x03;
    var sampleRateIndex = (b2 >> 2) & 0x0F;
    // b2 bit 1 = private bit (unused)
    var channelConfigHi = b2 & 0x01;

    var b3 = buffer[offset + 3];
    var channelConfigLo = (b3 >> 6) & 0x03;
    var channelConfiguration = (channelConfigHi << 2) | channelConfigLo;
    // b3 bits 5..2 = original, home, copyright id bit, copyright id start (unused)

    var frameLength =
      ((b3 & 0x03) << 11) |
      (buffer[offset + 4] << 3) |
      ((buffer[offset + 5] >> 5) & 0x07);

    var bufferFullness =
      ((buffer[offset + 5] & 0x1F) << 6) |
      ((buffer[offset + 6] >> 2) & 0x3F);

    var numberOfRawDataBlocks = buffer[offset + 6] & 0x03;

    var headerLength = protectionAbsent ? ShortHeaderLength : LongHeaderLength;
    if (buffer.Length - offset < headerLength)
      throw new InvalidDataException("Buffer too small for ADTS header with CRC.");

    if (sampleRateIndex >= SampleRateTable.Length)
      throw new InvalidDataException($"Invalid ADTS sample rate index {sampleRateIndex}.");
    var sampleRate = SampleRateTable[sampleRateIndex];
    if (sampleRate == 0 && sampleRateIndex < 13)
      throw new InvalidDataException($"Reserved ADTS sample rate index {sampleRateIndex}.");

    return new AdtsHeader(
      IsMpeg2: isMpeg2,
      ProtectionAbsent: protectionAbsent,
      Profile: profile,
      SampleRateIndex: sampleRateIndex,
      SampleRate: sampleRate,
      ChannelConfiguration: channelConfiguration,
      FrameLength: frameLength,
      BufferFullness: bufferFullness,
      NumberOfRawDataBlocks: numberOfRawDataBlocks,
      HeaderLengthBytes: headerLength);
  }

  /// <summary>Builds a 7-byte ADTS header with the given fields (used by tests).</summary>
  public static byte[] BuildHeader(
    int profile, int sampleRateIndex, int channelConfig, int frameLength,
    bool mpeg2 = false, int bufferFullness = 0x7FF, int numRawBlocks = 0) {
    var header = new byte[ShortHeaderLength];
    header[0] = 0xFF;
    header[1] = (byte)(0xF0 | (mpeg2 ? 0x08 : 0x00) | 0x01); // layer=0, protection absent=1
    header[2] = (byte)(((profile & 0x03) << 6) | ((sampleRateIndex & 0x0F) << 2) | ((channelConfig >> 2) & 0x01));
    header[3] = (byte)(((channelConfig & 0x03) << 6) | ((frameLength >> 11) & 0x03));
    header[4] = (byte)((frameLength >> 3) & 0xFF);
    header[5] = (byte)(((frameLength & 0x07) << 5) | ((bufferFullness >> 6) & 0x1F));
    header[6] = (byte)(((bufferFullness & 0x3F) << 2) | (numRawBlocks & 0x03));
    return header;
  }
}
