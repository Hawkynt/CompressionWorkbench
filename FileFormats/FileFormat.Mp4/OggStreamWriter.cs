#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Mp4;

/// <summary>
/// Minimal Ogg (RFC 3533) container writer used to re-wrap codec packets that an
/// MP4/MKV container stores bare (Opus, Vorbis) so they can be fed to decoders that
/// only accept an Ogg logical stream. Each supplied packet becomes one or more 255-byte
/// lacing segments; pages carry the correct CRC-32 (Ogg polynomial 0x04C11DB7, no
/// reflection, zero init/xor) so the output is a conformant stream, not just one our
/// own readers happen to tolerate.
/// </summary>
internal static class OggStreamWriter {
  private static readonly uint[] CrcTable = BuildCrcTable();

  /// <summary>
  /// Builds an Ogg stream from <paramref name="headerPackets"/> (each placed on its own
  /// page, BOS on the first) followed by <paramref name="audioPackets"/> (one page each,
  /// EOS on the last). <paramref name="granuleEnd"/> is written as the granule position of
  /// the final page; intermediate audio pages carry a monotonically interpolated value.
  /// </summary>
  internal static byte[] Build(uint serial, IReadOnlyList<byte[]> headerPackets,
                               IReadOnlyList<byte[]> audioPackets, ulong granuleEnd) {
    using var ms = new MemoryStream();
    uint seq = 0;

    for (var i = 0; i < headerPackets.Count; ++i) {
      var flags = i == 0 ? (byte)0x02 : (byte)0x00; // BOS on the first header page
      WritePage(ms, serial, seq++, flags, granule: 0, [headerPackets[i]]);
    }

    for (var i = 0; i < audioPackets.Count; ++i) {
      var last = i == audioPackets.Count - 1;
      var flags = last ? (byte)0x04 : (byte)0x00; // EOS on the last audio page
      var granule = last
        ? granuleEnd
        : (ulong)((double)granuleEnd * (i + 1) / Math.Max(1, audioPackets.Count));
      WritePage(ms, serial, seq++, flags, granule, [audioPackets[i]]);
    }

    return ms.ToArray();
  }

  private static void WritePage(Stream output, uint serial, uint seq, byte flags, ulong granule, byte[][] packets) {
    var segments = new List<byte>();
    using var body = new MemoryStream();
    foreach (var pkt in packets) {
      var remaining = pkt.Length;
      var written = 0;
      while (remaining >= 255) {
        segments.Add(255);
        body.Write(pkt, written, 255);
        written += 255;
        remaining -= 255;
      }
      segments.Add((byte)remaining);
      if (remaining > 0) body.Write(pkt, written, remaining);
    }

    var bodyBytes = body.ToArray();
    var page = new byte[27 + segments.Count + bodyBytes.Length];
    var s = page.AsSpan();
    "OggS"u8.CopyTo(s);
    s[4] = 0;      // stream structure version
    s[5] = flags;  // header type
    BinaryPrimitives.WriteUInt64LittleEndian(s[6..], granule);
    BinaryPrimitives.WriteUInt32LittleEndian(s[14..], serial);
    BinaryPrimitives.WriteUInt32LittleEndian(s[18..], seq);
    // s[22..26] CRC left zero for the checksum computation, filled below.
    s[26] = (byte)segments.Count;
    for (var i = 0; i < segments.Count; ++i) page[27 + i] = segments[i];
    bodyBytes.CopyTo(page, 27 + segments.Count);

    var crc = Crc(page);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc);
    output.Write(page, 0, page.Length);
  }

  private static uint Crc(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data)
      crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
    return crc;
  }

  private static uint[] BuildCrcTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var r = i << 24;
      for (var j = 0; j < 8; ++j)
        r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
      table[i] = r;
    }
    return table;
  }
}
