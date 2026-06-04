#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// Parsed fields of a DTS Coherent Acoustics (DCA) core frame header. The header that follows the
/// 32-bit 0x7FFE8001 sync word carries the frame byte size (FSIZE), the sample-block count (NBLKS),
/// the channel arrangement (AMODE), the sample-rate code (SFREQ), the transmission-bitrate code
/// (RATE), the subframe count and the LFE flag. This is the single canonical core-header parser
/// shared by the decoder and the read-only stream-info path; the field order follows the DTS
/// Coherent Acoustics bitstream specification and FFmpeg's <c>dca_parse_frame_header</c>.
/// <para>
/// Only the standard 16-bit big-endian framing (sync 0x7FFE8001) is supported. The 14-bit packed
/// (0x1FFFE800) and the byte-swapped little-endian (0xFE7F0180 / 0xFF1F00E8) framings are out of
/// scope and surface as an unparseable header.
/// </para>
/// </summary>
public readonly record struct DtsFrameHeader(
  int FrameType,
  int SampleBlocks,
  int FrameSize,
  int Amode,
  int SampleRate,
  int SampleRateCode,
  int BitRate,
  int BitRateIndex,
  bool CrcPresent,
  bool Aspf,
  int Lfe,
  bool PredictorHistory,
  int Subframes,
  int HeaderBitLength) {

  /// <summary>The 32-bit big-endian DTS core sync word (0x7FFE8001).</summary>
  public static readonly byte[] SyncWord = [0x7F, 0xFE, 0x80, 0x01];

  /// <summary>SFREQ → sample rate in Hz (FFmpeg <c>avpriv_dca_sample_rates</c>; 0 = reserved).</summary>
  private static readonly int[] SampleRates = DtsTables.SampleRates;

  /// <summary>RATE → constant transmission bitrate in bit/s (open/variable/lossless map to 1/2/3 sentinels).</summary>
  private static readonly int[] BitRates = DtsTables.BitRates;

  /// <summary>
  /// AMODE → human-readable channel arrangement (before the optional LFE channel). The codes
  /// follow the DTS core "audio channel arrangement" table.
  /// </summary>
  public static string AmodeName(int amode) => amode switch {
    0 => "A (mono)",
    1 => "A+B (dual mono)",
    2 => "L+R (stereo)",
    3 => "(L+R)+(L-R) (sum/difference stereo)",
    4 => "LT+RT (total stereo)",
    5 => "C+L+R (3.0)",
    6 => "L+R+S (2.1 surround)",
    7 => "C+L+R+S (3.1 surround)",
    8 => "L+R+SL+SR (quad)",
    9 => "C+L+R+SL+SR (5.0)",
    10 => "CL+CR+L+R+SL+SR (6.0)",
    11 => "C+L+R+LR+RR+OV (6.1)",
    12 => "CF+CR+LF+RF+LR+RR (6.0 front/rear)",
    13 => "CL+C+CR+L+R+SL+SR (7.0)",
    14 => "CL+CR+L+R+SL1+SL2+SR1+SR2 (8.0)",
    15 => "CL+C+CR+L+R+SL+S+SR (8.0 alt)",
    _ => $"user-defined ({amode})",
  };

  /// <summary>Channel count implied by an AMODE code (excluding the LFE channel).</summary>
  public static int AmodeChannelCount(int amode) => amode switch {
    0 => 1, 1 => 2, 2 => 2, 3 => 2, 4 => 2, 5 => 3, 6 => 3, 7 => 4,
    8 => 4, 9 => 5, 10 => 6, 11 => 6, 12 => 6, 13 => 7, 14 => 8, 15 => 8,
    _ => 0,
  };

  /// <summary>
  /// Parses a DTS core header from <paramref name="data"/> at <paramref name="offset"/>, which
  /// must point at the 0x7FFE8001 sync word. Returns <see langword="null"/> if there is not
  /// enough data, the sync word is wrong, the sample rate is reserved, or the bitrate is invalid.
  /// </summary>
  public static DtsFrameHeader? TryParse(ReadOnlySpan<byte> data, int offset) {
    if (offset < 0 || offset + 14 > data.Length)
      return null;
    if (data[offset] != 0x7F || data[offset + 1] != 0xFE
        || data[offset + 2] != 0x80 || data[offset + 3] != 0x01)
      return null;

    // Bound the reader to the available span; header-only / truncated inputs simply stop short.
    var buffer = data.Slice(offset, Math.Min(data.Length - offset, 32)).ToArray();
    DtsBitReader r;
    try {
      r = new DtsBitReader(buffer, 0, buffer.Length);
      r.SkipBits(32);                          // sync word

      var ftype = (int)r.ReadBits(1);          // FTYPE — frame type
      r.SkipBits(5);                           // SHORT — deficit sample count
      var cpf = r.ReadFlag();                  // CPF   — CRC present flag
      var nblks = (int)r.ReadBits(7);          // NBLKS — (sample blocks - 1)
      var fsize = (int)r.ReadBits(14);         // FSIZE — (frame byte size - 1)
      var amode = (int)r.ReadBits(6);          // AMODE — channel arrangement
      var sfreq = (int)r.ReadBits(4);          // SFREQ — core sample-rate code
      var rate = (int)r.ReadBits(5);           // RATE  — transmission bitrate code
      r.SkipBits(1);                           // FixedBit (reserved)
      r.SkipBits(1);                           // DYNF  — embedded dynamic range
      r.SkipBits(1);                           // TIMEF — embedded time stamp
      r.SkipBits(1);                           // AUXF  — auxiliary data
      r.SkipBits(1);                           // HDCD
      r.SkipBits(3);                           // EXT_AUDIO_ID
      r.SkipBits(1);                           // EXT_AUDIO
      var aspf = r.ReadFlag();                 // ASPF  — sync-word position in subsubframes
      var lff = (int)r.ReadBits(2);            // LFF   — low-frequency effects flag
      var predHistory = r.ReadFlag();          // predictor history switch
      if (cpf)
        r.SkipBits(16);                        // header CRC
      r.SkipBits(1);                           // MULTIRATE_INTER (perfect-reconstruction QMF select)
      r.SkipBits(4);                           // VERSION
      r.SkipBits(2);                           // COPY_HISTORY
      r.SkipBits(3);                           // PCM source resolution
      r.SkipBits(1);                           // front sum/difference
      r.SkipBits(1);                           // surround sum/difference
      r.SkipBits(4);                           // dialog normalisation
      var subframes = (int)r.ReadBits(4) + 1;  // SUBFS — subframe count

      var sampleRate = sfreq < SampleRates.Length ? SampleRates[sfreq] : 0;
      if (sampleRate == 0)
        return null;
      // RATE codes 29/30/31 (open / variable / lossless) carry the sentinels 1/2/3; a real
      // constant bitrate maps to a positive value. Either is a valid header for stream info.
      var bitrate = rate < BitRates.Length ? BitRates[rate] : 0;
      if (bitrate == 0)
        return null;
      // FSIZE is (size - 1). The DTS spec minimum is 95 bytes; we accept the structural minimum
      // (a parseable 14-byte header) so synthetic / truncated streams still surface stream info.
      var frameSize = fsize + 1;
      if (frameSize < 14 || lff > 2)
        return null;

      return new DtsFrameHeader(
        FrameType: ftype,
        SampleBlocks: nblks + 1,
        FrameSize: frameSize,
        Amode: amode,
        SampleRate: sampleRate,
        SampleRateCode: sfreq,
        BitRate: bitrate,
        BitRateIndex: rate,
        CrcPresent: cpf,
        Aspf: aspf,
        Lfe: lff,
        PredictorHistory: predHistory,
        Subframes: subframes,
        HeaderBitLength: (int)r.BitPosition);
    } catch (InvalidDataException) {
      return null;
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }
}
