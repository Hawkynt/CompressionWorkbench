#pragma warning disable CS1591

namespace Codec.Opus;

/// <summary>
/// One of the three Opus operating modes, decoded from the TOC byte's config
/// field per RFC 6716 §3.1.
/// </summary>
public enum OpusMode {
  SilkOnly,
  Hybrid,
  CeltOnly,
}

/// <summary>
/// Opus audio bandwidth (NB/MB/WB/SWB/FB) as signalled by the TOC config field.
/// </summary>
public enum OpusBandwidth {
  /// <summary>Narrowband — 4 kHz audio bandwidth, 8 kHz sample rate.</summary>
  Narrowband,
  /// <summary>Mediumband — 6 kHz audio bandwidth, 12 kHz sample rate.</summary>
  Mediumband,
  /// <summary>Wideband — 8 kHz audio bandwidth, 16 kHz sample rate.</summary>
  Wideband,
  /// <summary>Super-wideband — 12 kHz audio bandwidth, 24 kHz sample rate.</summary>
  SuperWideband,
  /// <summary>Fullband — 20 kHz audio bandwidth, 48 kHz sample rate.</summary>
  Fullband,
}

/// <summary>
/// Decoded TOC-byte configuration for an Opus packet (RFC 6716 Table 2).
/// </summary>
/// <param name="Config">Raw config field (0-31).</param>
/// <param name="Mode">SILK-only / Hybrid / CELT-only.</param>
/// <param name="Bandwidth">Signalled audio bandwidth.</param>
/// <param name="FrameDurationMicros">Frame duration in microseconds (2500 / 5000 / 10000 / 20000 / 40000 / 60000).</param>
/// <param name="IsStereo">Stereo flag (bit 2 of the TOC byte, mislabeled "s" in the RFC).</param>
/// <param name="FrameCountCode">Frame-packing code c (bits 0-1 of the TOC byte).</param>
public readonly record struct OpusTocInfo(
  int Config,
  OpusMode Mode,
  OpusBandwidth Bandwidth,
  int FrameDurationMicros,
  bool IsStereo,
  int FrameCountCode) {

  /// <summary>Samples per frame at the 48 kHz native CELT output rate.</summary>
  public int FrameSamplesAt48k => (int)(48L * FrameDurationMicros / 1000L);
}

/// <summary>
/// Parses the TOC byte (RFC 6716 §3.1) and walks the four frame-packing
/// formats (codes 0-3, §3.2).
/// </summary>
public static class OpusPacketReader {

  /// <summary>
  /// Parses an Opus TOC byte into <see cref="OpusTocInfo"/>.
  /// </summary>
  public static OpusTocInfo ParseToc(byte toc) {
    var config = (toc >> 3) & 0x1F;
    var stereo = (toc & 0x04) != 0;
    var code = toc & 0x03;

    OpusMode mode;
    OpusBandwidth bw;
    int durMicros;

    // RFC 6716 Table 2
    if (config < 12) {
      mode = OpusMode.SilkOnly;
      bw = config < 4 ? OpusBandwidth.Narrowband
         : config < 8 ? OpusBandwidth.Mediumband
                     : OpusBandwidth.Wideband;
      durMicros = (config & 0x03) switch {
        0 => 10000,
        1 => 20000,
        2 => 40000,
        _ => 60000,
      };
    } else if (config < 16) {
      mode = OpusMode.Hybrid;
      bw = config < 14 ? OpusBandwidth.SuperWideband : OpusBandwidth.Fullband;
      durMicros = (config & 0x01) == 0 ? 10000 : 20000;
    } else {
      mode = OpusMode.CeltOnly;
      var idx = (config - 16) / 4;
      bw = idx switch {
        0 => OpusBandwidth.Narrowband,
        1 => OpusBandwidth.Wideband,
        2 => OpusBandwidth.SuperWideband,
        _ => OpusBandwidth.Fullband,
      };
      durMicros = (config & 0x03) switch {
        0 => 2500,
        1 => 5000,
        2 => 10000,
        _ => 20000,
      };
    }

    return new OpusTocInfo(config, mode, bw, durMicros, stereo, code);
  }

  /// <summary>
  /// Counts the number of Opus frames packed into <paramref name="packet"/>
  /// per RFC 6716 §3.2. Returns 0 if the packet is malformed.
  /// </summary>
  public static int CountFrames(ReadOnlySpan<byte> packet) {
    if (packet.Length < 1) return 0;
    var toc = ParseToc(packet[0]);
    return toc.FrameCountCode switch {
      0 => 1,
      1 => 2,
      2 => 2,
      _ => packet.Length < 2 ? 0 : (packet[1] & 0x3F),
    };
  }

  /// <summary>
  /// Splits an Opus packet into individual frame byte ranges per RFC 6716 §3.2,
  /// respecting codes 0 (1 frame), 1 (2 CBR frames), 2 (2 VBR frames), and
  /// 3 (N frames — CBR or VBR, optional padding).
  /// </summary>
  public static List<Range> SplitFrames(ReadOnlySpan<byte> packet) {
    var ranges = new List<Range>();
    if (packet.Length < 1) return ranges;
    var toc = ParseToc(packet[0]);
    var pos = 1;

    switch (toc.FrameCountCode) {
      case 0:
        ranges.Add(new Range(pos, packet.Length));
        return ranges;

      case 1: {
        var len = packet.Length - 1;
        if ((len & 1) != 0) return ranges;
        var half = len / 2;
        ranges.Add(new Range(pos, pos + half));
        ranges.Add(new Range(pos + half, pos + 2 * half));
        return ranges;
      }

      case 2: {
        if (!TryReadFrameLength(packet, ref pos, out var n1)) return ranges;
        if (pos + n1 > packet.Length) return ranges;
        ranges.Add(new Range(pos, pos + n1));
        ranges.Add(new Range(pos + n1, packet.Length));
        return ranges;
      }

      default: {
        if (packet.Length < 2) return ranges;
        var b = packet[1];
        var vbr = (b & 0x80) != 0;
        var hasPadding = (b & 0x40) != 0;
        var count = b & 0x3F;
        pos = 2;

        var totalPadding = 0;
        if (hasPadding) {
          while (true) {
            if (pos >= packet.Length) return ranges;
            var p = packet[pos++];
            if (p == 255) totalPadding += 254;
            else { totalPadding += p; break; }
          }
        }

        if (vbr) {
          var lengths = new int[count - 1];
          for (var i = 0; i < count - 1; i++) {
            if (!TryReadFrameLength(packet, ref pos, out var ln)) return ranges;
            lengths[i] = ln;
          }
          var dataEnd = packet.Length - totalPadding;
          var cursor = pos;
          for (var i = 0; i < count - 1; i++) {
            if (cursor + lengths[i] > dataEnd) { ranges.Clear(); return ranges; }
            ranges.Add(new Range(cursor, cursor + lengths[i]));
            cursor += lengths[i];
          }
          if (cursor > dataEnd) { ranges.Clear(); return ranges; }
          ranges.Add(new Range(cursor, dataEnd));
        } else {
          var dataEnd = packet.Length - totalPadding;
          var remaining = dataEnd - pos;
          if (count <= 0 || remaining < 0 || remaining % count != 0) { ranges.Clear(); return ranges; }
          var frameLen = remaining / count;
          for (var i = 0; i < count; i++)
            ranges.Add(new Range(pos + i * frameLen, pos + (i + 1) * frameLen));
        }

        return ranges;
      }
    }
  }

  private static bool TryReadFrameLength(ReadOnlySpan<byte> packet, ref int pos, out int length) {
    length = 0;
    if (pos >= packet.Length) return false;
    var b0 = packet[pos++];
    if (b0 < 252) { length = b0; return true; }
    if (pos >= packet.Length) return false;
    var b1 = packet[pos++];
    length = b1 * 4 + b0;
    return true;
  }
}
