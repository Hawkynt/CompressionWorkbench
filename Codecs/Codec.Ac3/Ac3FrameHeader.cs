#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Parsed syncinfo + BSI (bit-stream information) of one AC-3 / E-AC-3 sync frame per ATSC A/52.
/// The 0x0B77 sync word is followed by crc1, fscod, frmsizecod (AC-3) — or strmtyp/frmsiz/fscod
/// (E-AC-3) — and then the BSI. <see cref="Bsid"/> ≤ 10 selects the legacy AC-3 layout;
/// <see cref="Bsid"/> 11..16 selects the E-AC-3 (Annex E) header. This is the single shared parser
/// used by both the decoder and the read-only stream-info path.
/// </summary>
public readonly record struct Ac3FrameHeader(
  bool IsEnhanced,
  int FrameSize,
  int SampleRate,
  int FsCod,
  int Bitrate,
  int Acmod,
  bool LowFrequencyEffects,
  int DialNorm,
  int Bsid,
  int StreamType = 0,
  int SubstreamId = 0,
  int NumBlocks = 6,
  int FsCod2 = -1) {

  /// <summary>E-AC-3 frame type 0 = independent substream (decodable on its own).</summary>
  public bool IsIndependentSubstream => !this.IsEnhanced || this.StreamType is 0 or 2;

  /// <summary>E-AC-3 frame type 1 = dependent substream (extends an earlier independent one).</summary>
  public bool IsDependentSubstream => this.IsEnhanced && this.StreamType == 1;

  /// <summary>The 16-bit big-endian AC-3 sync word (0x0B77).</summary>
  public static readonly byte[] SyncWord = [0x0B, 0x77];

  /// <summary>fscod → sample rate in Hz (code 3 is reserved for AC-3 → 0).</summary>
  private static readonly int[] SampleRates = [48000, 44100, 32000, 0];

  // E-AC-3 fscod 3 selects the half-rate table indexed by fscod2.
  private static readonly int[] HalfSampleRates = [24000, 22050, 16000, 0];

  /// <summary>
  /// frmsizecod → bitrate (kbit/s) and words-per-syncframe for fscod 0 / 1 / 2 (A/52 Table 5.18).
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
  /// Parses an AC-3 / E-AC-3 sync frame header at <paramref name="offset"/> (which must point at
  /// the 0x0B77 sync word). Returns <see langword="null"/> on insufficient data, a wrong sync word,
  /// or a reserved sample-rate / frame-size code.
  /// </summary>
  public static Ac3FrameHeader? TryParse(ReadOnlySpan<byte> data, int offset) {
    if (offset < 0 || offset + 6 > data.Length || data[offset] != 0x0B || data[offset + 1] != 0x77)
      return null;
    // The remaining BSI fields never need more than a few bytes; bounding the reader to the
    // available span keeps header-only / truncated inputs from over-reading.
    var span = data.Slice(offset);
    var buffer = span.ToArray();

    // Peek bsid (5 bits, located 40 bits after the sync word) to choose the header layout.
    var peek = new Ac3BitReader(buffer, 0, buffer.Length);
    peek.SkipBits(40);
    var bsid = (int)peek.ReadBits(5);

    return bsid <= 10 && bsid != 16
      ? ParseLegacy(buffer)
      : ParseEnhanced(buffer);
  }

  private static Ac3FrameHeader? ParseLegacy(byte[] buffer) {
    var r = new Ac3BitReader(buffer, 2, buffer.Length - 2);
    try {
      r.SkipBits(16);                          // crc1
      var fscod = (int)r.ReadBits(2);          // sample-rate code
      var frmsizecod = (int)r.ReadBits(6);     // frame-size code
      var bsid = (int)r.ReadBits(5);           // bit-stream identification
      r.SkipBits(3);                           // bsmod
      var acmod = (int)r.ReadBits(3);          // audio coding mode

      if ((acmod & 0x1) != 0 && acmod != 1)
        r.SkipBits(2);                         // cmixlev
      if ((acmod & 0x4) != 0)
        r.SkipBits(2);                         // surmixlev
      if (acmod == 2)
        r.SkipBits(2);                         // dsurmod

      var lfeon = r.ReadFlag();                // low-frequency effects channel
      var dialnorm = (int)r.ReadBits(5);       // dialogue normalization

      var sampleRate = fscod < SampleRates.Length ? SampleRates[fscod] : 0;
      if (sampleRate == 0 || frmsizecod >= FrameSizeTable.Length)
        return null;

      var entry = FrameSizeTable[frmsizecod];
      var words = fscod switch { 0 => entry.Words48, 1 => entry.Words441, _ => entry.Words32 };

      return new Ac3FrameHeader(
        IsEnhanced: false,
        FrameSize: words * 2,
        SampleRate: sampleRate,
        FsCod: fscod,
        Bitrate: entry.Kbps * 1000,
        Acmod: acmod,
        LowFrequencyEffects: lfeon,
        DialNorm: dialnorm,
        Bsid: bsid);
    } catch (InvalidDataException) {
      return null;
    }
  }

  private static Ac3FrameHeader? ParseEnhanced(byte[] buffer) {
    var r = new Ac3BitReader(buffer, 2, buffer.Length - 2);
    try {
      var strmtyp = (int)r.ReadBits(2);        // strmtyp (0 indep, 1 dependent, 2 indep/AC-3-converted)
      var substreamid = (int)r.ReadBits(3);    // substreamid
      var frmsiz = (int)r.ReadBits(11);        // (frame size in 16-bit words) - 1
      var fscod = (int)r.ReadBits(2);          // sample-rate code
      int sampleRate, numblkscod, fscod2 = -1;
      if (fscod == 3) {
        fscod2 = (int)r.ReadBits(2);           // half-rate sample-rate code
        sampleRate = fscod2 < HalfSampleRates.Length ? HalfSampleRates[fscod2] : 0;
        numblkscod = 3;                         // fscod==3 always carries 6 blocks
      } else {
        sampleRate = fscod < SampleRates.Length ? SampleRates[fscod] : 0;
        numblkscod = (int)r.ReadBits(2);
      }
      var acmod = (int)r.ReadBits(3);
      var lfeon = r.ReadFlag();
      var bsid = (int)r.ReadBits(5);            // bit-stream identification (11..16)
      var dialnorm = (int)r.ReadBits(5);

      if (sampleRate == 0 || bsid is < 11 or > 16)
        return null;

      var frameSize = (frmsiz + 1) * 2;
      var blocks = numblkscod switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };
      var samplesPerFrame = blocks * 256;
      var bitrate = samplesPerFrame > 0
        ? (int)((long)frameSize * 8 * sampleRate / samplesPerFrame)
        : 0;

      return new Ac3FrameHeader(
        IsEnhanced: true,
        FrameSize: frameSize,
        SampleRate: sampleRate,
        FsCod: fscod,
        Bitrate: bitrate,
        Acmod: acmod,
        LowFrequencyEffects: lfeon,
        DialNorm: dialnorm,
        Bsid: bsid,
        StreamType: strmtyp,
        SubstreamId: substreamid,
        NumBlocks: blocks,
        FsCod2: fscod2);
    } catch (InvalidDataException) {
      return null;
    }
  }
}
