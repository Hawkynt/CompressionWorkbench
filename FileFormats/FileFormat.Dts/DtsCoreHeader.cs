#pragma warning disable CS1591
namespace FileFormat.Dts;

/// <summary>
/// Parsed fields of a DTS Coherent Acoustics core frame header. The 14-byte (big-endian)
/// header that follows the 0x7FFE8001 sync word carries everything needed for stream-info:
/// the frame byte size (FSIZE), the channel arrangement (AMODE), the sample-rate code (SFREQ),
/// the transmission-bitrate code (RATE) and the LFE flag. Field positions and the
/// AMODE/SFREQ/RATE tables follow the DTS Coherent Acoustics bitstream specification.
/// </summary>
public readonly record struct DtsCoreHeader(
  int FrameSize,
  int SampleBlocks,
  int Amode,
  int SampleRate,
  int Bitrate,
  bool LowFrequencyEffects) {

  /// <summary>The 32-bit big-endian DTS core sync word.</summary>
  public static readonly byte[] SyncWord = [0x7F, 0xFE, 0x80, 0x01];

  /// <summary>SFREQ → sample rate in Hz (index 0 and reserved entries map to 0).</summary>
  private static readonly int[] SampleRates = [
    0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0,
    12000, 24000, 48000, 96000, 192000,
  ];

  /// <summary>RATE → constant transmission bitrate in bit/s (open / variable / lossless map to 0).</summary>
  private static readonly int[] Bitrates = [
    32000, 56000, 64000, 96000, 112000, 128000, 192000, 224000,
    256000, 320000, 384000, 448000, 512000, 576000, 640000, 768000,
    960000, 1024000, 1152000, 1280000, 1344000, 1408000, 1411200, 1472000,
    1509750, 1920000, 2048000, 3072000, 3840000, 0 /*open*/, 0 /*variable*/, 0 /*lossless*/,
  ];

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
  /// enough data, the sync word is wrong, or the decoded sample rate is reserved.
  /// </summary>
  public static DtsCoreHeader? TryParse(ReadOnlySpan<byte> data, int offset) {
    if (offset < 0 || offset + 14 > data.Length)
      return null;
    if (data[offset] != 0x7F || data[offset + 1] != 0xFE
        || data[offset + 2] != 0x80 || data[offset + 3] != 0x01)
      return null;

    var reader = new BigEndianBitReader(data, (offset + 4) * 8);

    reader.Skip(1);                  // FTYPE — frame type (1 = normal)
    reader.Skip(5);                  // SHORT — deficit sample count
    reader.Skip(1);                  // CPF   — CRC present flag
    var nblks = reader.Read(7);      // NBLKS — (sample blocks - 1)
    var fsize = reader.Read(14);     // FSIZE — (frame byte size - 1)
    var amode = reader.Read(6);      // AMODE — channel arrangement
    var sfreq = reader.Read(4);      // SFREQ — core sample-rate code
    var rate = reader.Read(5);       // RATE  — transmission bitrate code
    reader.Skip(1);                  // FixedBit
    reader.Skip(1);                  // DYNF  — embedded dynamic range
    reader.Skip(1);                  // TIMEF — embedded time stamp
    reader.Skip(1);                  // AUXF  — auxiliary data
    reader.Skip(1);                  // HDCD
    reader.Skip(3);                  // EXT_AUDIO_ID
    reader.Skip(1);                  // EXT_AUDIO
    reader.Skip(1);                  // ASPF  — sync-word position
    var lff = reader.Read(2);        // LFF   — low-frequency effects flag

    var sampleRate = sfreq < SampleRates.Length ? SampleRates[sfreq] : 0;
    if (sampleRate == 0)
      return null;

    var bitrate = rate < Bitrates.Length ? Bitrates[rate] : 0;
    var frameSize = fsize + 1;        // FSIZE is (size - 1)
    if (frameSize < 14)
      return null;

    return new DtsCoreHeader(
      FrameSize: frameSize,
      SampleBlocks: nblks + 1,
      Amode: amode,
      SampleRate: sampleRate,
      Bitrate: bitrate,
      LowFrequencyEffects: lff is 1 or 2);
  }
}

/// <summary>Minimal MSB-first big-endian bit reader over a byte span (DTS/AC-3 header parsing).</summary>
internal ref struct BigEndianBitReader(ReadOnlySpan<byte> data, int bitPosition) {
  private readonly ReadOnlySpan<byte> _data = data;
  private int _pos = bitPosition;

  public void Skip(int bits) => _pos += bits;

  public int Read(int bits) {
    var value = 0;
    for (var i = 0; i < bits; ++i) {
      var bytePos = _pos >> 3;
      var bitInByte = 7 - (_pos & 7);
      var bit = bytePos < _data.Length ? (_data[bytePos] >> bitInByte) & 1 : 0;
      value = (value << 1) | bit;
      ++_pos;
    }
    return value;
  }
}
