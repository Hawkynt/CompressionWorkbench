using System.Buffers.Binary;

namespace FileFormat.Dms;

/// <summary>The 56-byte DMS file header (big-endian).</summary>
/// <param name="Id">Bytes 4–7: <c>" PRO"</c> or <c>"FILE"</c>.</param>
/// <param name="InfoFlags">Bit 1 encrypted, bit 2 appended archives, bit 3 banner, bit 4 high density, bit 8 FILE_ID.DIZ.</param>
/// <param name="Date">Creation time, Unix seconds.</param>
public sealed record DmsHeader(
  uint Id, uint InfoFlags, uint Date, ushort LowTrack, ushort HighTrack, uint PackedSize, uint UnpackedSize,
  ushort OsVersion, ushort OsRevision, ushort Cpu, ushort Fpu, ushort Machine, ushort DiskType2, ushort CpuSpeed,
  uint CreationTime, ushort CreatorVersion, ushort NeededVersion, ushort DiskType, ushort CompressionMode) {

  public bool IsEncrypted => (this.InfoFlags & DmsConstants.InfoEncrypted) != 0;

  internal static DmsHeader Parse(ReadOnlySpan<byte> b) => new(
    BinaryPrimitives.ReadUInt32BigEndian(b[4..]), BinaryPrimitives.ReadUInt32BigEndian(b[8..]),
    BinaryPrimitives.ReadUInt32BigEndian(b[12..]), BinaryPrimitives.ReadUInt16BigEndian(b[16..]),
    BinaryPrimitives.ReadUInt16BigEndian(b[18..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..]),
    BinaryPrimitives.ReadUInt32BigEndian(b[24..]), BinaryPrimitives.ReadUInt16BigEndian(b[28..]),
    BinaryPrimitives.ReadUInt16BigEndian(b[30..]), BinaryPrimitives.ReadUInt16BigEndian(b[32..]),
    BinaryPrimitives.ReadUInt16BigEndian(b[34..]), BinaryPrimitives.ReadUInt16BigEndian(b[36..]),
    BinaryPrimitives.ReadUInt16BigEndian(b[38..]), BinaryPrimitives.ReadUInt16BigEndian(b[40..]),
    BinaryPrimitives.ReadUInt32BigEndian(b[42..]), BinaryPrimitives.ReadUInt16BigEndian(b[46..]),
    BinaryPrimitives.ReadUInt16BigEndian(b[48..]), BinaryPrimitives.ReadUInt16BigEndian(b[50..]),
    BinaryPrimitives.ReadUInt16BigEndian(b[52..]));

  internal void Write(Span<byte> b) {
    b[..DmsConstants.FileHeaderSize].Clear();
    BinaryPrimitives.WriteUInt32BigEndian(b, DmsConstants.MagicValue);
    BinaryPrimitives.WriteUInt32BigEndian(b[4..], this.Id);
    BinaryPrimitives.WriteUInt32BigEndian(b[8..], this.InfoFlags);
    BinaryPrimitives.WriteUInt32BigEndian(b[12..], this.Date);
    BinaryPrimitives.WriteUInt16BigEndian(b[16..], this.LowTrack);
    BinaryPrimitives.WriteUInt16BigEndian(b[18..], this.HighTrack);
    BinaryPrimitives.WriteUInt32BigEndian(b[20..], this.PackedSize);
    BinaryPrimitives.WriteUInt32BigEndian(b[24..], this.UnpackedSize);
    BinaryPrimitives.WriteUInt16BigEndian(b[28..], this.OsVersion);
    BinaryPrimitives.WriteUInt16BigEndian(b[30..], this.OsRevision);
    BinaryPrimitives.WriteUInt16BigEndian(b[32..], this.Cpu);
    BinaryPrimitives.WriteUInt16BigEndian(b[34..], this.Fpu);
    BinaryPrimitives.WriteUInt16BigEndian(b[36..], this.Machine);
    BinaryPrimitives.WriteUInt16BigEndian(b[38..], this.DiskType2);
    BinaryPrimitives.WriteUInt16BigEndian(b[40..], this.CpuSpeed);
    BinaryPrimitives.WriteUInt32BigEndian(b[42..], this.CreationTime);
    BinaryPrimitives.WriteUInt16BigEndian(b[46..], this.CreatorVersion);
    BinaryPrimitives.WriteUInt16BigEndian(b[48..], this.NeededVersion);
    BinaryPrimitives.WriteUInt16BigEndian(b[50..], this.DiskType);
    BinaryPrimitives.WriteUInt16BigEndian(b[52..], this.CompressionMode);
    BinaryPrimitives.WriteUInt16BigEndian(b[54..], DmsCrc.Compute(b[4..54]));
  }
}

/// <summary>The 20-byte header in front of every DMS track (big-endian).</summary>
/// <param name="Number">Signed: 0–79 cylinders, 80 FILE_ID.DIZ, negative banner text.</param>
/// <param name="PackedSize">Bytes stored in the file after the header.</param>
/// <param name="RuntimeSize">Size after the first decompression stage (before RLE).</param>
/// <param name="UnpackedSize">Decoded track size.</param>
/// <param name="DataChecksum">16-bit sum of the decoded bytes.</param>
/// <param name="PackedCrc">CRC-16 of the stored bytes.</param>
public sealed record DmsTrackHeader(
  short Number, ushort PackedSize, ushort RuntimeSize, ushort UnpackedSize, byte Flags, byte Mode,
  ushort DataChecksum, ushort PackedCrc, long DataOffset) {

  /// <summary>Banner, FILE_ID.DIZ or the 1 KiB advertising pseudo boot block — text, not disk data.</summary>
  public bool IsText => this.Number < 0 || this.Number == DmsConstants.TrackDiz || (this.Number == 0 && this.UnpackedSize == 1024);

  internal static void Write(Span<byte> b, short number, ushort packed, ushort runtime, ushort unpacked, byte flags, byte mode, ushort dataSum, ushort packedCrc) {
    b[..DmsConstants.TrackHeaderSize].Clear();
    BinaryPrimitives.WriteUInt16BigEndian(b, DmsConstants.TrackSignature);
    BinaryPrimitives.WriteInt16BigEndian(b[2..], number);
    BinaryPrimitives.WriteUInt16BigEndian(b[6..], packed);
    BinaryPrimitives.WriteUInt16BigEndian(b[8..], runtime);
    BinaryPrimitives.WriteUInt16BigEndian(b[10..], unpacked);
    b[12] = flags;
    b[13] = mode;
    BinaryPrimitives.WriteUInt16BigEndian(b[14..], dataSum);
    BinaryPrimitives.WriteUInt16BigEndian(b[16..], packedCrc);
    BinaryPrimitives.WriteUInt16BigEndian(b[18..], DmsCrc.Compute(b[..18]));
  }
}

/// <summary>CRC-16/ARC (reflected polynomial 0xA001, initial 0) — DMS's header and packed-data CRC.</summary>
internal static class DmsCrc {
  private static readonly ushort[] Table = Build();

  private static ushort[] Build() {
    var t = new ushort[256];
    for (var i = 0; i < 256; ++i) {
      var c = (uint)i;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1;
      t[i] = (ushort)c;
    }
    return t;
  }

  public static ushort Compute(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data)
      crc = (ushort)((crc >> 8) ^ Table[(byte)(crc ^ b)]);
    return crc;
  }

  /// <summary>DMS's "uncrunched CRC": a plain 16-bit sum of the decoded bytes.</summary>
  public static ushort Sum(ReadOnlySpan<byte> data) {
    ushort sum = 0;
    foreach (var b in data) sum += b;
    return sum;
  }
}
