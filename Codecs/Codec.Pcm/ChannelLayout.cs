namespace Codec.Pcm;

/// <summary>
/// Speaker-channel model for multi-channel audio, mirroring FFmpeg's
/// <c>libavutil/channel_layout</c>. Channel identities are bit positions shared by
/// <c>WAVE_FORMAT_EXTENSIBLE.dwChannelMask</c>, CAF's channel bitmap and FFmpeg's
/// <c>AVChannel</c> (bits 0–17 are the WAVE speakers, 29+ are FFmpeg extensions up
/// to the 22.2 bottom speakers). For streams that don't carry an explicit speaker
/// mask, <see cref="DefaultNames"/> applies FFmpeg's
/// <c>av_channel_layout_default</c> rule — the first entry of its layout map with a
/// matching channel count (mono, stereo, 2.1, 4.0, 5.0, 5.1, 6.1, 7.1, 5.1.4,
/// 7.1.4, 9.1.4, 9.1.6, 22.2) — and any unmapped count degrades to indexed
/// <c>CH_n</c> names so arbitrary channel counts stay decodable.
/// </summary>
public static class ChannelLayout {

  // Bit positions follow WAVE dwChannelMask / FFmpeg AVChannel.
  private const int FrontLeft = 0;
  private const int FrontRight = 1;
  private const int FrontCenter = 2;
  private const int LowFrequency = 3;
  private const int BackLeft = 4;
  private const int BackRight = 5;
  private const int FrontLeftOfCenter = 6;
  private const int FrontRightOfCenter = 7;
  private const int BackCenter = 8;
  private const int SideLeft = 9;
  private const int SideRight = 10;
  private const int TopCenter = 11;
  private const int TopFrontLeft = 12;
  private const int TopFrontCenter = 13;
  private const int TopFrontRight = 14;
  private const int TopBackLeft = 15;
  private const int TopBackCenter = 16;
  private const int TopBackRight = 17;
  private const int DownmixLeft = 29;
  private const int DownmixRight = 30;
  private const int WideLeft = 31;
  private const int WideRight = 32;
  private const int SurroundDirectLeft = 33;
  private const int SurroundDirectRight = 34;
  private const int LowFrequency2 = 35;
  private const int TopSideLeft = 36;
  private const int TopSideRight = 37;
  private const int BottomFrontCenter = 38;
  private const int BottomFrontLeft = 39;
  private const int BottomFrontRight = 40;
  private const int SideSurroundLeft = 41;
  private const int SideSurroundRight = 42;
  private const int TopSurroundLeft = 43;
  private const int TopSurroundRight = 44;

  /// <summary>Canonical per-speaker pseudo-file names, indexed by channel bit.</summary>
  private static readonly Dictionary<int, string> _namesByBit = new() {
    [FrontLeft] = "FRONT_LEFT",
    [FrontRight] = "FRONT_RIGHT",
    [FrontCenter] = "CENTER",
    [LowFrequency] = "LFE",
    [BackLeft] = "BACK_LEFT",
    [BackRight] = "BACK_RIGHT",
    [FrontLeftOfCenter] = "FRONT_LEFT_OF_CENTER",
    [FrontRightOfCenter] = "FRONT_RIGHT_OF_CENTER",
    [BackCenter] = "BACK_CENTER",
    [SideLeft] = "SIDE_LEFT",
    [SideRight] = "SIDE_RIGHT",
    [TopCenter] = "TOP_CENTER",
    [TopFrontLeft] = "TOP_FRONT_LEFT",
    [TopFrontCenter] = "TOP_FRONT_CENTER",
    [TopFrontRight] = "TOP_FRONT_RIGHT",
    [TopBackLeft] = "TOP_BACK_LEFT",
    [TopBackCenter] = "TOP_BACK_CENTER",
    [TopBackRight] = "TOP_BACK_RIGHT",
    [DownmixLeft] = "DOWNMIX_LEFT",
    [DownmixRight] = "DOWNMIX_RIGHT",
    [WideLeft] = "WIDE_LEFT",
    [WideRight] = "WIDE_RIGHT",
    [SurroundDirectLeft] = "SURROUND_DIRECT_LEFT",
    [SurroundDirectRight] = "SURROUND_DIRECT_RIGHT",
    [LowFrequency2] = "LFE2",
    [TopSideLeft] = "TOP_SIDE_LEFT",
    [TopSideRight] = "TOP_SIDE_RIGHT",
    [BottomFrontCenter] = "BOTTOM_FRONT_CENTER",
    [BottomFrontLeft] = "BOTTOM_FRONT_LEFT",
    [BottomFrontRight] = "BOTTOM_FRONT_RIGHT",
    [SideSurroundLeft] = "SIDE_SURROUND_LEFT",
    [SideSurroundRight] = "SIDE_SURROUND_RIGHT",
    [TopSurroundLeft] = "TOP_SURROUND_LEFT",
    [TopSurroundRight] = "TOP_SURROUND_RIGHT",
  };

  private static readonly Dictionary<string, int> _bitsByName = BuildNameIndex();

  private static Dictionary<string, int> BuildNameIndex() {
    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var (bit, name) in _namesByBit)
      map[name] = bit;
    // Legacy aliases used by mono/stereo pseudo-files.
    map["MONO"] = FrontLeft;
    map["LEFT"] = FrontLeft;
    map["RIGHT"] = FrontRight;
    map["FRONT_CENTER"] = FrontCenter;
    return map;
  }

  private static ulong Bits(params int[] bits) {
    var mask = 0UL;
    foreach (var b in bits)
      mask |= 1UL << b;
    return mask;
  }

  // FFmpeg av_channel_layout_default: the first layout-map entry per channel count.
  private static readonly Dictionary<int, ulong> _defaultMaskByCount = new() {
    [1] = Bits(FrontCenter),                                                       // mono
    [2] = Bits(FrontLeft, FrontRight),                                             // stereo
    [3] = Bits(FrontLeft, FrontRight, LowFrequency),                               // 2.1
    [4] = Bits(FrontLeft, FrontRight, FrontCenter, BackCenter),                    // 4.0
    [5] = Bits(FrontLeft, FrontRight, FrontCenter, BackLeft, BackRight),           // 5.0
    [6] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight), // 5.1
    [7] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackCenter, SideLeft, SideRight), // 6.1
    [8] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight, SideLeft, SideRight), // 7.1
    [10] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, SideLeft, SideRight,
                TopFrontLeft, TopFrontRight, TopBackLeft, TopBackRight),           // 5.1.4
    [12] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight,
                SideLeft, SideRight, TopFrontLeft, TopFrontRight, TopBackLeft, TopBackRight), // 7.1.4
    [14] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight,
                FrontLeftOfCenter, FrontRightOfCenter, SideLeft, SideRight,
                TopFrontLeft, TopFrontRight, TopBackLeft, TopBackRight),           // 9.1.4
    [16] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight,
                FrontLeftOfCenter, FrontRightOfCenter, SideLeft, SideRight,
                TopFrontLeft, TopFrontRight, TopBackLeft, TopBackRight,
                TopSideLeft, TopSideRight),                                        // 9.1.6
    [24] = Bits(FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight,
                FrontLeftOfCenter, FrontRightOfCenter, BackCenter, SideLeft, SideRight,
                TopCenter, TopFrontLeft, TopFrontCenter, TopFrontRight,
                TopBackLeft, TopBackCenter, TopBackRight,
                LowFrequency2, TopSideLeft, TopSideRight,
                BottomFrontCenter, BottomFrontLeft, BottomFrontRight),             // 22.2
  };

  /// <summary>
  /// Per-channel pseudo-file names for a stream WITHOUT an explicit speaker mask:
  /// FFmpeg's default layout for the count, or <c>CH_n</c> for unmapped counts.
  /// Mono keeps <c>MONO</c> and plain stereo keeps <c>LEFT</c>/<c>RIGHT</c>.
  /// </summary>
  public static IReadOnlyList<string> DefaultNames(int channels) {
    if (channels == 1) return ["MONO"];
    if (channels == 2) return ["LEFT", "RIGHT"];
    return _defaultMaskByCount.TryGetValue(channels, out var mask)
      ? NamesInBitOrder(mask)
      : Enumerable.Range(0, channels).Select(i => $"CH_{i}").ToArray();
  }

  /// <summary>
  /// Per-channel names from an explicit speaker mask (WAVE_FORMAT_EXTENSIBLE
  /// <c>dwChannelMask</c>, CAF channel bitmap). The mask wins only when its
  /// population count matches the actual channel count; otherwise the count
  /// defaults apply. Plain stereo (FL|FR) keeps the legacy LEFT/RIGHT names.
  /// </summary>
  public static IReadOnlyList<string> NamesFromMask(ulong mask, int channels) {
    if (System.Numerics.BitOperations.PopCount(mask) != channels)
      return DefaultNames(channels);
    if (channels == 1) return ["MONO"];
    if (channels == 2 && mask == Bits(FrontLeft, FrontRight)) return ["LEFT", "RIGHT"];
    return NamesInBitOrder(mask);
  }

  /// <summary>
  /// Canonical interleave position of a named channel — the inverse of the
  /// naming above, used to sort per-channel inputs back into file order when
  /// assembling a multi-channel file. <c>CH_n</c> maps to <c>n</c>; unknown
  /// names sort last.
  /// </summary>
  public static int OrderIndex(string name) {
    if (_bitsByName.TryGetValue(name, out var bit))
      return bit;
    if (name.StartsWith("CH_", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name.AsSpan(3), out var index) && index >= 0)
      return index;
    return int.MaxValue;
  }

  private static IReadOnlyList<string> NamesInBitOrder(ulong mask) {
    var result = new List<string>();
    for (var bit = 0; bit < 64 && mask >> bit != 0; ++bit) {
      if ((mask & (1UL << bit)) == 0)
        continue;
      result.Add(_namesByBit.TryGetValue(bit, out var name) ? name : $"CH_{bit}");
    }
    return result;
  }
}
