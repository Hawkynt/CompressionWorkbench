#pragma warning disable CS1591
using Codec.Ac3;

namespace FileFormat.Ac3;

/// <summary>
/// Parsed header fields of one AC-3 / E-AC-3 sync frame. The 0x0B77 sync word is followed by
/// syncinfo + BSI (bit-stream information). <c>bsid</c> ≤ 10 selects the legacy AC-3 header layout
/// (ATSC A/52); <c>bsid</c> = 16 selects the E-AC-3 (Annex E) header. This is a thin descriptor-side
/// view over the shared <see cref="Ac3FrameHeader"/> parser in <c>Codec.Ac3</c>, so the syncinfo /
/// BSI tables live in one place and stay consistent between the decoder and the stream-info path.
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
  public static readonly byte[] SyncWord = Ac3FrameHeader.SyncWord;

  /// <summary>acmod → human-readable channel arrangement (before the optional LFE channel).</summary>
  public static string AcmodName(int acmod) => Ac3FrameHeader.AcmodName(acmod);

  /// <summary>Number of full-bandwidth channels implied by acmod (excludes LFE).</summary>
  public static int AcmodChannelCount(int acmod) => Ac3FrameHeader.AcmodChannelCount(acmod);

  /// <summary>Friendly layout name including the LFE channel (e.g. "3/2 + LFE (5.1)").</summary>
  public static string LayoutName(int acmod, bool lfe) => Ac3FrameHeader.LayoutName(acmod, lfe);

  /// <summary>
  /// Parses an AC-3 / E-AC-3 sync frame header at <paramref name="offset"/> (which must point at the
  /// 0x0B77 sync word). Returns <see langword="null"/> on insufficient data, a wrong sync word, or a
  /// reserved sample-rate / frame-size code.
  /// </summary>
  public static Ac3SyncFrame? TryParse(ReadOnlySpan<byte> data, int offset)
    => Ac3FrameHeader.TryParse(data, offset) is { } h
      ? new Ac3SyncFrame(h.IsEnhanced, h.FrameSize, h.SampleRate, h.Bitrate, h.Acmod,
                         h.LowFrequencyEffects, h.DialNorm, h.Bsid)
      : null;
}
