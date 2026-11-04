using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Woz;

/// <summary>
/// Writes WOZ 2 images from the WOZ 2 reference (applesaucefdc.com/woz/reference2): the 12-byte
/// header with a CRC-32 of everything after it, an INFO chunk (60 bytes), a TMAP chunk (160
/// quarter-track slots), a TRKS chunk whose 160-entry TRK table points at 512-byte blocks of
/// bitstream data starting at block 3, and an optional META chunk.
/// </summary>
public static class WozWriter {
  public const int InfoSize = 60;
  public const int TmapSize = 160;
  private const int TrkTableSize = 160 * 8;

  /// <summary>A default WOZ 2 INFO block for a 5.25" disk.</summary>
  public static byte[] DefaultInfo(byte diskType = 1) {
    var info = new byte[InfoSize];
    info[0] = 2;            // INFO version
    info[1] = diskType;     // 1 = 5.25", 2 = 3.5"
    Encoding.ASCII.GetBytes("CompressionWorkbench".PadRight(32)).CopyTo(info, 5);
    info[37] = 1;           // disk sides
    info[39] = (byte)(diskType == 2 ? 16 : 32); // optimal bit timing (125 ns units)
    return info;
  }

  /// <summary>The usual map: whole track t at quarter-tracks 4t−1, 4t and 4t+1.</summary>
  public static byte[] DefaultTmap(IEnumerable<int> tracks) {
    var tmap = new byte[TmapSize];
    Array.Fill(tmap, (byte)0xFF);
    foreach (var t in tracks)
      for (var q = 4 * t - 1; q <= 4 * t + 1; ++q)
        if (q is >= 0 and < TmapSize && (tmap[q] == 0xFF || q == 4 * t)) tmap[q] = (byte)t;
    return tmap;
  }

  /// <param name="info">60-byte INFO payload; a version-1 INFO is upgraded to version 2.</param>
  /// <param name="tracks">TRK index → (bitstream bytes, bit count).</param>
  public static void Write(Stream output, byte[] info, byte[] tmap, IReadOnlyDictionary<int, (byte[] Bits, uint BitCount)> tracks, string? meta) {
    if (info.Length != InfoSize) throw new ArgumentException("WOZ INFO is 60 bytes.", nameof(info));
    if (tmap.Length != TmapSize) throw new ArgumentException("WOZ TMAP is 160 bytes.", nameof(tmap));
    if (tracks.Keys.Any(k => k is < 0 or >= 160)) throw new ArgumentException("WOZ holds TRK entries 0–159.", nameof(tracks));
    foreach (var (k, (bits, count)) in tracks)
      if (count == 0 || (count + 7) / 8 > bits.Length) throw new ArgumentException($"WOZ track {k}: bit count {count} does not fit {bits.Length} bytes.");

    info = (byte[])info.Clone();
    if (info[0] < 2) info[0] = 2;
    var trkTable = new byte[TrkTableSize];
    var data = new MemoryStream();
    var block = 3;
    var largest = 0;
    foreach (var (k, (bits, count)) in tracks.OrderBy(t => t.Key)) {
      var bytes = (int)((count + 7) / 8);
      var blocks = (bytes + 511) / 512;
      BinaryPrimitives.WriteUInt16LittleEndian(trkTable.AsSpan(8 * k), (ushort)block);
      BinaryPrimitives.WriteUInt16LittleEndian(trkTable.AsSpan(8 * k + 2), (ushort)blocks);
      BinaryPrimitives.WriteUInt32LittleEndian(trkTable.AsSpan(8 * k + 4), count);
      data.Write(bits, 0, bytes);
      data.Write(new byte[blocks * 512 - bytes]);
      block += blocks;
      largest = Math.Max(largest, blocks);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(44), (ushort)largest);

    var body = new MemoryStream();
    Chunk(body, "INFO", info);
    Chunk(body, "TMAP", tmap);
    var trks = new byte[TrkTableSize + data.Length];
    trkTable.CopyTo(trks, 0);
    data.ToArray().CopyTo(trks, TrkTableSize);
    Chunk(body, "TRKS", trks);
    if (!string.IsNullOrEmpty(meta)) Chunk(body, "META", Encoding.UTF8.GetBytes(meta));

    var bodyBytes = body.ToArray();
    var header = new byte[12];
    "WOZ2"u8.CopyTo(header);
    header[4] = 0xFF; header[5] = 0x0A; header[6] = 0x0D; header[7] = 0x0A;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Crc32(bodyBytes));
    output.Write(header);
    output.Write(bodyBytes);
  }

  private static void Chunk(Stream s, string id, ReadOnlySpan<byte> payload) {
    Span<byte> h = stackalloc byte[8];
    Encoding.ASCII.GetBytes(id, h);
    BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)payload.Length);
    s.Write(h);
    s.Write(payload);
  }

  internal static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
    }
    return ~crc;
  }
}
