#pragma warning disable CS1591

namespace Codec.Mp3;

/// <summary>
/// Parsed MPEG-1/2/2.5 Layer I/II/III frame header. MP3 frames begin with a 32-bit
/// header: 11 bits sync (0xFFE or 0xFFF), 2 bits version ID, 2 bits layer, 1 bit
/// protection (CRC), 4 bits bitrate index, 2 bits sample-rate index, 1 bit padding,
/// 1 bit private, 2 bits channel mode, 2 bits mode extension, 1 bit copyright,
/// 1 bit original, 2 bits emphasis. Fields are raw; sample rate / bitrate lookups
/// must go through <see cref="SampleRateHz"/> and <see cref="BitrateKbps"/>.
/// </summary>
public readonly record struct Mp3FrameHeader(
  int VersionId,     // 0=MPEG-2.5, 2=MPEG-2 LSF, 3=MPEG-1
  int Layer,         // 1=Layer III, 2=Layer II, 3=Layer I (after inversion from raw bits)
  bool HasCrc,
  int BitrateIndex,
  int SampleRateIndex,
  bool Padding,
  bool Private,
  int ChannelMode,   // 0=stereo, 1=joint-stereo, 2=dual-channel, 3=mono
  int ModeExtension, // joint-stereo: bit0=intensity, bit1=MS
  bool Copyright,
  bool Original,
  int Emphasis) {

  /// <summary>
  /// Parses a 4-byte MP3 frame header. Throws <see cref="InvalidDataException"/> if
  /// the syncword is absent or reserved bit patterns are present.
  /// </summary>
  public static Mp3FrameHeader Parse(ReadOnlySpan<byte> header4) {
    if (header4.Length < 4) throw new ArgumentException("Header must be 4 bytes.", nameof(header4));
    var h0 = header4[0];
    var h1 = header4[1];
    var h2 = header4[2];
    var h3 = header4[3];

    // 11-bit syncword: 0xFFF (MPEG-1/2) or 0xFFE (MPEG-2.5). Accept both (high 11 bits ≥ 0x7FE).
    if (h0 != 0xFF || (h1 & 0xE0) != 0xE0)
      throw new InvalidDataException("MP3 frame syncword not found (expected 0xFFFx/0xFFEx).");

    var versionId = (h1 >> 3) & 0x03;  // 0=2.5, 1=reserved, 2=MPEG-2, 3=MPEG-1
    if (versionId == 1) throw new InvalidDataException("Reserved MPEG version ID in frame header.");

    var layerBits = (h1 >> 1) & 0x03;  // 0=reserved, 1=Layer III, 2=Layer II, 3=Layer I
    if (layerBits == 0) throw new InvalidDataException("Reserved Layer value in frame header.");
    var layer = 4 - layerBits;         // 1=Layer I, 2=Layer II, 3=Layer III

    var hasCrc = (h1 & 0x01) == 0;     // protection bit: 0 = CRC present

    var bitrateIndex = (h2 >> 4) & 0x0F;
    if (bitrateIndex == 0x0F) throw new InvalidDataException("Reserved bitrate index 1111 in frame header.");

    var sampleRateIndex = (h2 >> 2) & 0x03;
    if (sampleRateIndex == 0x03) throw new InvalidDataException("Reserved sample-rate index 11 in frame header.");

    var padding = ((h2 >> 1) & 1) != 0;
    var @private = (h2 & 1) != 0;
    var channelMode = (h3 >> 6) & 0x03;
    var modeExtension = (h3 >> 4) & 0x03;
    var copyright = ((h3 >> 3) & 1) != 0;
    var original = ((h3 >> 2) & 1) != 0;
    var emphasis = h3 & 0x03;

    return new Mp3FrameHeader(versionId, layer, hasCrc, bitrateIndex, sampleRateIndex,
      padding, @private, channelMode, modeExtension, copyright, original, emphasis);
  }

  /// <summary>True for MPEG-1 (version id = 3).</summary>
  public bool IsMpeg1 => this.VersionId == 3;

  /// <summary>True for MPEG-2.5 (unofficial extension, version id = 0).</summary>
  public bool IsMpeg25 => this.VersionId == 0;

  /// <summary>True when this frame carries only a single audio channel.</summary>
  public bool IsMono => this.ChannelMode == 3;

  /// <summary>True when joint-stereo mode has MS-stereo enabled.</summary>
  public bool IsMsStereo => this.ChannelMode == 1 && (this.ModeExtension & 0x02) != 0;

  /// <summary>True when joint-stereo mode has intensity-stereo enabled.</summary>
  public bool IsIntensityStereo => this.ChannelMode == 1 && (this.ModeExtension & 0x01) != 0;

  /// <summary>Number of channels (1 for mono, 2 for any stereo mode).</summary>
  public int Channels => this.IsMono ? 1 : 2;

  /// <summary>Sample rate in Hz after version scaling (MPEG-2 halves, MPEG-2.5 quarters).</summary>
  public int SampleRateHz {
    get {
      var baseHz = this.SampleRateIndex switch { 0 => 44100, 1 => 48000, 2 => 32000, _ => 0 };
      if (baseHz == 0) return 0;
      var shift = (this.IsMpeg1 ? 0 : 1) + (this.IsMpeg25 ? 1 : 0);
      return baseHz >> shift;
    }
  }

  /// <summary>Bitrate in kbps from the MPEG/layer-specific lookup table (0 = free format).</summary>
  public int BitrateKbps => LookupBitrate(this.IsMpeg1, this.Layer, this.BitrateIndex);

  /// <summary>Samples produced per decoded frame (1152 for MPEG-1 L2/3, 576 for MPEG-2 L3, 384 for L1).</summary>
  public int SamplesPerFrame {
    get {
      if (this.Layer == 1) return 384;
      // MPEG-1 L2/3 = 1152; MPEG-2/2.5 Layer III = 576; MPEG-2 Layer II = 1152.
      if (this.Layer == 3 && !this.IsMpeg1) return 576;
      return 1152;
    }
  }

  /// <summary>
  /// Frame length in bytes (including header). Returns 0 for free-format frames.
  /// Formula: samplesPerFrame * bitrate / sampleRate / 8 + padding.
  /// </summary>
  public int FrameLengthBytes {
    get {
      var kbps = this.BitrateKbps;
      if (kbps == 0) return 0; // free format — caller must scan for next sync
      var bytes = this.SamplesPerFrame * kbps * 125 / this.SampleRateHz; // kbps*125 = bits per ms * 1000 / 8
      if (this.Layer == 1) {
        bytes &= ~3;
        if (this.Padding) bytes += 4;
      } else if (this.Padding) {
        bytes += 1;
      }
      return bytes;
    }
  }

  // halfrate[mpeg1][layerRow][bitrateIndex] × 2 = kbps. The row order matches minimp3's
  // table layout: row 0 = Layer III, row 1 = Layer II, row 2 = Layer I (i.e. ordered
  // by raw layer-bits ascending: 01 = III, 10 = II, 11 = I). The previous version's
  // row comments were flipped (saying "Layer I" for the row that holds Layer III's
  // rates) and the lookup used `[layer-1]` which inverted the mapping — that's why
  // a hand-crafted MPEG-1 Layer III @ 128 kbps (idx 9) was reporting as 288 kbps
  // (the value at MPEG-1 Layer I row idx 9).
  private static readonly byte[,,] _HalfRateTable = {
    { // MPEG-2 / MPEG-2.5
      { 0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80 },        // Layer III
      { 0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80 },        // Layer II
      { 0, 16, 24, 28, 32, 40, 48, 56, 64, 72, 80, 88, 96, 112, 128 }     // Layer I
    },
    { // MPEG-1
      { 0, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160 },   // Layer III
      { 0, 16, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192 },  // Layer II
      { 0, 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224 } // Layer I
    }
  };

  private static int LookupBitrate(bool mpeg1, int layer, int bitrateIndex) {
    if (bitrateIndex == 0 || bitrateIndex >= 15) return 0; // free / reserved
    // layer is 1=Layer I, 2=Layer II, 3=Layer III; row index is 3-layer (Layer III → row 0).
    return 2 * _HalfRateTable[mpeg1 ? 1 : 0, 3 - layer, bitrateIndex];
  }
}
