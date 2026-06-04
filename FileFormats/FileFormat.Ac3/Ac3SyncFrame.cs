#pragma warning disable CS1591
namespace FileFormat.Ac3;

/// <summary>
/// Parsed header fields of one AC-3 / E-AC-3 sync frame. The 0x0B77 sync word is followed by
/// syncinfo + BSI (bit-stream information). <c>bsid</c> ≤ 8 selects the legacy AC-3 header
/// layout (ATSC A/52); <c>bsid</c> = 16 selects the E-AC-3 (Annex E) header. Frame size, sample
/// rate, bitrate, channel arrangement (acmod), LFE flag and dialnorm are extracted; the
/// acmod/fscod/frmsizecod tables follow the A/52 specification.
/// </summary>
public readonly record struct Ac3SyncFrame(
  bool IsEnhanced,
  int FrameSize,
  int SampleRate,
  int Bitrate,
  int Acmod,
  bool LowFrequencyEffects,
  int DialNorm,
  int Bsid) {

  /// <summary>The 16-bit big-endian AC-3 sync word (0x0B77).</summary>
  public static readonly byte[] SyncWord = [0x0B, 0x77];

  /// <summary>fscod → sample rate in Hz (code 3 is reserved → 0).</summary>
  private static readonly int[] SampleRates = [48000, 44100, 32000, 0];

  /// <summary>
  /// frmsizecod → (frame size in 16-bit words) for each of the three fscod values, plus the
  /// nominal bitrate. The A/52 frame-size table is indexed by frmsizecod ≥ 1 in pairs
  /// (two frmsizecod codes per bitrate). Here we store, per frmsizecod, the bitrate in
  /// kbit/s and the words-per-syncframe for 48 / 44.1 / 32 kHz.
  /// </summary>
  private static readonly (int Kbps, int Words48, int Words441, int Words32)[] FrameSizeTable = [
    (32, 64, 69, 96), (32, 64, 70, 96),
    (40, 80, 87, 120), (40, 80, 88, 120),
    (48, 96, 104, 144), (48, 96, 105, 144),
    (56, 112, 121, 168), (56, 112, 122, 168),
    (64, 128, 139, 192), (64, 128, 140, 192),
    (80, 160, 174, 240), (80, 160, 175, 240),
    (96, 192, 208, 288), (96, 192, 209, 288),
    (112, 224, 243, 336), (112, 224, 244, 336),
    (128, 256, 278, 384), (128, 256, 279, 384),
    (160, 320, 348, 480), (160, 320, 349, 480),
    (192, 384, 417, 576), (192, 384, 418, 576),
    (224, 448, 487, 672), (224, 448, 488, 672),
    (256, 512, 557, 768), (256, 512, 558, 768),
    (320, 640, 696, 960), (320, 640, 697, 960),
    (384, 768, 835, 1152), (384, 768, 836, 1152),
    (448, 896, 975, 1344), (448, 896, 976, 1344),
    (512, 1024, 1114, 1536), (512, 1024, 1115, 1536),
    (576, 1152, 1253, 1728), (576, 1152, 1254, 1728),
    (640, 1280, 1393, 1920), (640, 1280, 1394, 1920),
  ];

  /// <summary>acmod → human-readable channel arrangement (before the optional LFE channel).</summary>
  public static string AcmodName(int acmod) => acmod switch {
    0 => "1+1 (dual mono)",
    1 => "1/0 (mono)",
    2 => "2/0 (stereo)",
    3 => "3/0 (L C R)",
    4 => "2/1 (L R S)",
    5 => "3/1 (L C R S)",
    6 => "2/2 (L R SL SR)",
    7 => "3/2 (L C R SL SR)",
    _ => $"reserved ({acmod})",
  };

  /// <summary>Number of full-bandwidth channels implied by acmod (excludes LFE).</summary>
  public static int AcmodChannelCount(int acmod) => acmod switch {
    0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, 7 => 5, _ => 0,
  };

  /// <summary>Friendly layout name including the LFE channel (e.g. "3/2 + LFE (5.1)").</summary>
  public static string LayoutName(int acmod, bool lfe) {
    var baseName = AcmodName(acmod);
    if (!lfe)
      return baseName;
    var total = AcmodChannelCount(acmod) + 1;
    var dot = acmod switch {
      7 => " (5.1)",
      6 or 5 => " (4.1)",
      _ => $" ({total} ch)",
    };
    return $"{baseName} + LFE{dot}";
  }

  /// <summary>
  /// Parses an AC-3 / E-AC-3 sync frame header at <paramref name="offset"/> (which must point
  /// at the 0x0B77 sync word). Returns <see langword="null"/> on insufficient data, a wrong
  /// sync word, or a reserved sample-rate / frame-size code.
  /// </summary>
  public static Ac3SyncFrame? TryParse(ReadOnlySpan<byte> data, int offset) {
    if (offset < 0 || offset + 6 > data.Length)
      return null;
    if (data[offset] != 0x0B || data[offset + 1] != 0x77)
      return null;

    // Peek bsid (5 bits at bit 40 from the sync word) to choose the header layout.
    var peek = new Ac3BitReader(data, (offset + 5) * 8);
    var bsid = peek.Read(5);

    return bsid <= 10 && bsid != 16
      ? ParseLegacy(data, offset, bsid)
      : ParseEnhanced(data, offset);
  }

  private static Ac3SyncFrame? ParseLegacy(ReadOnlySpan<byte> data, int offset, int peekBsid) {
    var r = new Ac3BitReader(data, (offset + 2) * 8);
    r.Skip(16);                  // crc1
    var fscod = r.Read(2);       // sample-rate code
    var frmsizecod = r.Read(6);  // frame-size code
    var bsid = r.Read(5);        // bit-stream identification
    r.Skip(3);                   // bsmod
    var acmod = r.Read(3);       // audio coding mode (channel arrangement)

    if ((acmod & 0x1) != 0 && acmod != 1)
      r.Skip(2);                 // cmixlev
    if ((acmod & 0x4) != 0)
      r.Skip(2);                 // surmixlev
    if (acmod == 2)
      r.Skip(2);                 // dsurmod

    var lfeon = r.Read(1);       // low-frequency effects channel
    var dialnorm = r.Read(5);    // dialogue normalization

    var sampleRate = fscod < SampleRates.Length ? SampleRates[fscod] : 0;
    if (sampleRate == 0 || frmsizecod >= FrameSizeTable.Length)
      return null;

    var entry = FrameSizeTable[frmsizecod];
    var words = fscod switch { 0 => entry.Words48, 1 => entry.Words441, _ => entry.Words32 };

    return new Ac3SyncFrame(
      IsEnhanced: false,
      FrameSize: words * 2,
      SampleRate: sampleRate,
      Bitrate: entry.Kbps * 1000,
      Acmod: acmod,
      LowFrequencyEffects: lfeon != 0,
      DialNorm: dialnorm,
      Bsid: bsid);
  }

  // E-AC-3 sample-rate code 3 selects the half-rate table indexed by fscod2.
  private static readonly int[] HalfSampleRates = [24000, 22050, 16000, 0];

  private static Ac3SyncFrame? ParseEnhanced(ReadOnlySpan<byte> data, int offset) {
    var r = new Ac3BitReader(data, (offset + 2) * 8);
    r.Skip(2);                   // strmtyp
    r.Skip(3);                   // substreamid
    var frmsiz = r.Read(11);     // (frame size in 16-bit words) - 1
    var fscod = r.Read(2);       // sample-rate code
    int sampleRate;
    int numblkscod;
    if (fscod == 3) {
      var fscod2 = r.Read(2);
      sampleRate = fscod2 < HalfSampleRates.Length ? HalfSampleRates[fscod2] : 0;
      numblkscod = 3;            // 6 blocks per frame at the reduced rate
    } else {
      sampleRate = fscod < SampleRates.Length ? SampleRates[fscod] : 0;
      numblkscod = r.Read(2);
    }
    var acmod = r.Read(3);       // audio coding mode
    var lfeon = r.Read(1);       // low-frequency effects channel
    r.Skip(5);                   // bsid (already known = 16)
    var dialnorm = r.Read(5);    // dialogue normalization

    if (sampleRate == 0)
      return null;

    var frameSize = (frmsiz + 1) * 2;
    var blocks = numblkscod switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };
    var samplesPerFrame = blocks * 256;
    var bitrate = samplesPerFrame > 0
      ? (int)((long)frameSize * 8 * sampleRate / samplesPerFrame)
      : 0;

    return new Ac3SyncFrame(
      IsEnhanced: true,
      FrameSize: frameSize,
      SampleRate: sampleRate,
      Bitrate: bitrate,
      Acmod: acmod,
      LowFrequencyEffects: lfeon != 0,
      DialNorm: dialnorm,
      Bsid: 16);
  }
}

/// <summary>Minimal MSB-first big-endian bit reader over a byte span (AC-3 header parsing).</summary>
internal ref struct Ac3BitReader(ReadOnlySpan<byte> data, int bitPosition) {
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
