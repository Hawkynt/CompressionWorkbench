#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Msa;

/// <summary>
/// Creates MSA (Magic Shadow Archiver) disk images from raw ST disk data.
/// Uses RLE compression per track.
/// </summary>
public static class MsaWriter {
  private const int SectorSize = 512;

  public static void Write(Stream output, byte[] diskData,
      ushort sectorsPerTrack = 9, ushort sides = 1) {
    var trackSize = sectorsPerTrack * SectorSize;
    var numSides = sides + 1;
    var totalTracks = diskData.Length / trackSize;
    var numTracksPerSide = totalTracks / numSides;

    var header = new byte[10];
    BinaryPrimitives.WriteUInt16BigEndian(header, MsaReader.MsaMagic);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), sectorsPerTrack);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), sides);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 0); // start track
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)(numTracksPerSide - 1)); // end track
    output.Write(header);

    for (var t = 0; t < totalTracks; t++) {
      var trackData = diskData.AsSpan(t * trackSize, trackSize);
      var compressed = CompressRle(trackData);

      if (compressed.Length < trackSize) {
        var lenBuf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)compressed.Length);
        output.Write(lenBuf);
        output.Write(compressed);
      } else {
        var lenBuf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)trackSize);
        output.Write(lenBuf);
        output.Write(trackData);
      }
    }
  }

  private static byte[] CompressRle(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    var i = 0;
    while (i < data.Length) {
      var b = data[i];
      var count = 1;
      while (i + count < data.Length && data[i + count] == b && count < 65535)
        count++;

      if (count >= 4 || b == 0xE5) {
        ms.WriteByte(0xE5);
        ms.WriteByte(b);
        ms.WriteByte((byte)(count >> 8));
        ms.WriteByte((byte)count);
      } else {
        for (var j = 0; j < count; j++)
          ms.WriteByte(b);
      }
      i += count;
    }
    return ms.ToArray();
  }
}
