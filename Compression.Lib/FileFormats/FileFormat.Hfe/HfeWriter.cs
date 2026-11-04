using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Hfe;

/// <summary>
/// Builds HFE images (HxC Floppy Emulator, revision 1 "HXCPICFE" and v3 "HXCHFEV3") from a header
/// description and per-track blocks. Layout from the published HFE file format specification
/// (HxC2001), cross-checked against Greaseweazle's <c>image/hfe.py</c> (public domain): a 512-byte header block (unused bytes 0xFF), the track
/// list at block 1, and each track's side-interleaved block (256 bytes per side, alternating) at a
/// 512-byte boundary, padded with 0xFF.
/// </summary>
public static class HfeWriter {
  /// <summary>The 26 meaningful header bytes, editable through <c>metadata.ini</c>.</summary>
  public sealed record Header(
    bool Version3 = false, byte FormatRevision = 0, byte Sides = 2, byte TrackEncoding = 0, ushort BitRateKbps = 250,
    ushort Rpm = 0, byte InterfaceMode = 7, byte WriteAllowed = 0xFF, byte SingleStep = 0xFF,
    byte Track0Side0AltEncoding = 0xFF, byte Track0Side0Encoding = 0xFF, byte Track0Side1AltEncoding = 0xFF, byte Track0Side1Encoding = 0xFF) {

    public static Header Parse(ReadOnlySpan<byte> h) => new(
      h[..8].SequenceEqual("HXCHFEV3"u8), h[8], h[10], h[11], BinaryPrimitives.ReadUInt16LittleEndian(h[12..]),
      BinaryPrimitives.ReadUInt16LittleEndian(h[14..]), h[16], h[20], h[21], h[22], h[23], h[24], h[25]);

    /// <summary>Applies <c>metadata.ini</c> keys as the reader renders them (decimal, 0x-hex or an encoding name).</summary>
    public Header With(byte[] ini) {
      var map = Encoding.UTF8.GetString(ini).Split('\n').Select(l => l.TrimEnd('\r'))
        .Where(l => l.Contains('=') && !l.StartsWith('[')).Select(l => l.Split('=', 2))
        .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim(), StringComparer.Ordinal);
      int Num(string key, int fallback) {
        if (!map.TryGetValue(key, out var v) || v.Length == 0) return fallback;
        var names = HfeFormatDescriptor.EncodingNamesForWriter;
        var named = Array.IndexOf(names, v);
        if (named >= 0 && named < names.Length - 1) return named;
        return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
          ? int.Parse(v.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
          : int.Parse(v, CultureInfo.InvariantCulture);
      }
      return new Header(
        Num("version", this.Version3 ? 3 : 1) == 3, (byte)Num("format_revision", this.FormatRevision), (byte)Num("sides", this.Sides),
        (byte)Num("track_encoding", this.TrackEncoding), (ushort)Num("bit_rate_kbps", this.BitRateKbps), (ushort)Num("floppy_rpm", this.Rpm),
        (byte)Num("interface_mode", this.InterfaceMode), (byte)Num("write_allowed", this.WriteAllowed), (byte)Num("single_step", this.SingleStep),
        (byte)Num("track0_side0_alt_encoding", this.Track0Side0AltEncoding), (byte)Num("track0_side0_encoding", this.Track0Side0Encoding),
        (byte)Num("track0_side1_alt_encoding", this.Track0Side1AltEncoding), (byte)Num("track0_side1_encoding", this.Track0Side1Encoding));
    }
  }

  public static void Write(Stream output, Header header, IReadOnlyList<byte[]> tracks) {
    if (tracks.Count is 0 or > 255) throw new ArgumentException("HFE holds 1 to 255 tracks.", nameof(tracks));
    if (tracks.Any(t => t.Length is 0 or > 0xFFFF)) throw new ArgumentException("HFE track blocks hold 1 to 65 535 bytes.", nameof(tracks));
    var lutBlocks = (tracks.Count * 4 + 511) / 512;
    var block = new byte[512];
    Array.Fill(block, (byte)0xFF);
    (header.Version3 ? "HXCHFEV3"u8 : "HXCPICFE"u8).CopyTo(block);
    block[8] = header.FormatRevision;
    block[9] = (byte)tracks.Count;
    block[10] = header.Sides;
    block[11] = header.TrackEncoding;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), header.BitRateKbps);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), header.Rpm);
    block[16] = header.InterfaceMode;
    block[17] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(18), 1);
    block[20] = header.WriteAllowed;
    block[21] = header.SingleStep;
    block[22] = header.Track0Side0AltEncoding;
    block[23] = header.Track0Side0Encoding;
    block[24] = header.Track0Side1AltEncoding;
    block[25] = header.Track0Side1Encoding;
    output.Write(block);

    var lut = new byte[lutBlocks * 512];
    Array.Fill(lut, (byte)0xFF);
    var next = 1 + lutBlocks;
    for (var t = 0; t < tracks.Count; ++t) {
      BinaryPrimitives.WriteUInt16LittleEndian(lut.AsSpan(4 * t), checked((ushort)next));
      BinaryPrimitives.WriteUInt16LittleEndian(lut.AsSpan(4 * t + 2), (ushort)tracks[t].Length);
      next += (tracks[t].Length + 511) / 512;
    }
    output.Write(lut);
    foreach (var t in tracks) {
      output.Write(t);
      var pad = (512 - t.Length % 512) % 512;
      if (pad > 0) {
        var fill = new byte[pad];
        Array.Fill(fill, (byte)0xFF);
        output.Write(fill);
      }
    }
  }
}
