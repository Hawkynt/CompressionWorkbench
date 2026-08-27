#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>Small in-place edits shared by CRUD and layout maintenance.</summary>
internal static class BcacheFsSuperblockEditor {

  internal static void Restamp(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureWritable(image);
    foreach (var slot in SuperblockSlots(image)) {
      var sb = ReadSuperblock(image, slot);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(104), (ulong)slot);
      Stamp(sb);
      image.Position = slot * SectorSize;
      image.Write(sb);
    }
    image.Flush();
  }

  internal static void SetLabel(Stream image, string label) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(label);
    EnsureWritable(image);

    var labelBytes = Encoding.ASCII.GetBytes(label);
    if (labelBytes.Length > 31)
      throw new ArgumentOutOfRangeException(nameof(label), "A bcachefs label is at most 31 ASCII bytes.");

    image.Position = PrimarySbSector * SectorSize + 112;
    Span<byte> seqBuffer = stackalloc byte[8];
    image.ReadExactly(seqBuffer);
    var seq = BinaryPrimitives.ReadUInt64LittleEndian(seqBuffer) + 1;

    foreach (var slot in SuperblockSlots(image)) {
      var sb = ReadSuperblock(image, slot);
      sb.AsSpan(72, 32).Clear();
      labelBytes.CopyTo(sb.AsSpan(72));
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(104), (ulong)slot);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(112), seq);
      Stamp(sb);
      image.Position = slot * SectorSize;
      image.Write(sb);
    }
    image.Flush();
  }

  private static long[] SuperblockSlots(Stream image) {
    var deviceSectors = image.Length / SectorSize;
    return [PrimarySbSector, PrimarySbSector + SbSlotSectors, deviceSectors - SbSlotSectors];
  }

  private static byte[] ReadSuperblock(Stream image, long slot) {
    var fixedPart = new byte[SbFixedBytes];
    image.Position = slot * SectorSize;
    image.ReadExactly(fixedPart);
    if (!fixedPart.AsSpan(24, 16).SequenceEqual(Magic))
      throw new InvalidDataException($"bcachefs: missing superblock copy at sector {slot}.");

    var u64s = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(124));
    if (u64s > 1 << 20)
      throw new InvalidDataException("bcachefs: superblock variable section is implausibly large.");
    var sb = new byte[SbFixedBytes + checked((int)u64s * 8)];
    image.Position = slot * SectorSize;
    image.ReadExactly(sb);
    return sb;
  }

  private static void Stamp(byte[] sb) {
    BinaryPrimitives.WriteUInt64LittleEndian(sb, MetadataChecksum(sb.AsSpan(16)));
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(8), 0);
  }

  private static void EnsureWritable(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("bcachefs superblock maintenance needs a readable, writable, seekable stream.", nameof(image));
  }
}
