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

  /// <summary>
  /// Reads where the fixed structures are: every superblock slot the layout
  /// advertises, and the journal's runs of buckets.
  /// </summary>
  internal static BcacheFsAllocationBuilder.Geometry ReadGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var sb = ReadSuperblock(image, PrimarySbSector);
    var count = sb[SbLayoutOffset + 18];
    var slots = new List<long>(count);
    for (var i = 0; i < count; ++i)
      slots.Add((long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(SbLayoutOffset + 24 + 8 * i)));

    var journal = new List<(long Start, long Count)>();
    foreach (var (type, offset, length) in Sections(sb)) {
      if (type != FieldJournalV2) continue;
      for (var at = offset + 8; at + 16 <= offset + length; at += 16)
        journal.Add(((long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(at)),
          (long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(at + 8))));
    }

    return new BcacheFsAllocationBuilder.Geometry(image.Length / SectorSize, slots, journal);
  }

  /// <summary>
  /// Publishes a new metadata generation: the journal is replaced by the one entry
  /// a clean shutdown leaves, and every superblock copy gets a clean section naming
  /// <paramref name="published" />'s roots and totals, and the next sequence number.
  /// </summary>
  internal static void PublishClean(Stream image, BcacheFsMetadataCommit.Published published) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(published);
    EnsureWritable(image);

    var primary = ReadSuperblock(image, PrimarySbSector);
    var seq = BinaryPrimitives.ReadUInt64LittleEndian(primary.AsSpan(112)) + 1;
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(primary.AsSpan(40));
    WriteJournal(image, ReadGeometry(image), magic, published);
    foreach (var slot in SuperblockSlots(image)) {
      var sb = BcacheFsSuperblockComposer.ReplaceClean(ReadSuperblock(image, slot),
        published.Roots, published.Usage, published.Inodes);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(104), (ulong)slot);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(112), seq);
      Stamp(sb);
      image.Position = slot * SectorSize;
      image.Write(sb);
    }
    image.Flush();
  }

  /// <summary>Empties the journal and writes the one entry a clean shutdown leaves in its first bucket.</summary>
  internal static void WriteJournal(Stream image, BcacheFsAllocationBuilder.Geometry geometry, ulong magic,
      BcacheFsMetadataCommit.Published published) {
    if (geometry.JournalBuckets.Count == 0)
      throw new InvalidDataException("bcachefs: the superblock names no journal buckets.");
    foreach (var (start, count) in geometry.JournalBuckets)
      BcacheFsMetadataCommit.ZeroRange(image, start * BucketBytes, count * BucketBytes);
    var entry = BcacheFsSuperblockComposer.JournalEntry(magic, published.Roots, published.Usage, published.Inodes);
    image.Position = geometry.JournalBuckets[0].Start * BucketBytes;
    image.Write(entry);
  }

  internal static IEnumerable<(uint Type, int Offset, int Length)> Sections(byte[] sb) {
    var offset = SbFixedBytes;
    while (offset + 8 <= sb.Length) {
      var words = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(offset));
      if (words == 0) yield break;
      var length = checked((int)words * 8);
      if (offset + length > sb.Length) yield break;
      yield return (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(offset + 4)), offset, length);
      offset += length;
    }
  }

  private static long[] SuperblockSlots(Stream image) => [.. ReadGeometry(image).SuperblockSectors];

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
