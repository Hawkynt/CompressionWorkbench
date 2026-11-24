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
  internal static void PublishClean(Stream image, BcacheFsMetadataCommit.Published published)
    => PublishClean(BcacheFsDevices.Single(image), published);

  /// <summary>
  /// Publishes a new metadata generation on every member of <paramref name="devices" />: each
  /// member's journal is replaced by the one entry a clean shutdown leaves, and every superblock
  /// copy on it gets the replicas entries the generation names, a clean section with its roots
  /// and totals, and the next sequence number, the same on every member.
  /// </summary>
  internal static void PublishClean(BcacheFsDevices devices, BcacheFsMetadataCommit.Published published) {
    ArgumentNullException.ThrowIfNull(devices);
    ArgumentNullException.ThrowIfNull(published);
    foreach (var stream in devices.Streams.Values) EnsureWritable(stream);

    var primaries = devices.Indices.ToDictionary(d => d, d => ReadSuperblock(devices[d], PrimarySbSector));
    var seq = primaries.Values.Max(sb => BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(112))) + 1;
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(primaries.Values.First().AsSpan(40));

    // The shutdown entry goes to every member with a journal; together they are one
    // journal replicas entry, which the superblock has to declare.
    var journaled = devices.Indices.Where(d => devices.Geometry[d].JournalBuckets.Count != 0).ToArray();
    if (journaled.Length == 0)
      throw new InvalidDataException("bcachefs: the superblock names no journal buckets.");
    published.Usage.Add(new BcacheFsAllocationBuilder.ReplicaSet(DataJournal, journaled), 0);
    var replicas = BcacheFsSuperblockComposer.MergeReplicas(
      primaries.Values.SelectMany(BcacheFsSuperblockComposer.ReadReplicas), published.Usage);

    foreach (var device in journaled)
      WriteJournal(devices[device], devices.Geometry[device], magic, published, replicas);
    foreach (var device in devices.Indices) {
      var image = devices[device];
      foreach (var slot in devices.Geometry[device].SuperblockSectors) {
        var sb = BcacheFsSuperblockComposer.ReplaceClean(ReadSuperblock(image, slot),
          published.Roots, published.Usage, published.Inodes, replicas);
        BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(104), (ulong)slot);
        BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(112), seq);
        StampMemberSequences(sb, seq);
        if (BcacheFsBucketGeometry.Accounting) MarkBtreeBitmaps(sb, published.Nodes);
        Stamp(sb);
        image.Position = slot * SectorSize;
        image.Write(sb);
      }
    }
    devices.Flush();
  }

  /// <summary>
  /// From metadata version 1.4 (<c>member_seq</c>) every member record carries the superblock
  /// sequence its device was last written at, and a kernel opening the members together calls a
  /// device whose own superblock is newer than its record says split-brained. Every member is
  /// written by a commit, so every record gets the commit's sequence; a volume whose records
  /// carry none (1.3) is left so.
  /// </summary>
  /// <summary>
  /// From 1.7 (<c>mi_btree_bitmap</c>) each member record says, one bit per
  /// <c>1 &lt;&lt; btree_bitmap_shift</c> sectors of its device, where b-tree nodes may be, and fsck
  /// wants every node inside a marked region. The regions holding <paramref name="nodes" /> are
  /// marked on top of what the record had; a node past the 64 regions the bitmap covers widens
  /// them, each bit then standing for the two it replaces.
  /// </summary>
  private static void MarkBtreeBitmaps(byte[] sb, IReadOnlyList<BcacheFsAllocationBuilder.Node> nodes) {
    const int shiftOffset = 28, bitmapOffset = 128;
    foreach (var (type, offset, length) in Sections(sb)) {
      if (type != FieldMembersV2 || length < 16) continue;
      var memberBytes = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(offset + 8));
      if (memberBytes < bitmapOffset + 8) continue;
      var index = 0;
      for (var at = offset + 16; at + memberBytes <= offset + length; at += memberBytes, ++index) {
        var mine = nodes.Where(n => n.Device == index).ToList();
        if (mine.Count == 0) continue;
        var shift = (int)sb[at + shiftOffset];
        var bitmap = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(at + bitmapOffset));
        var end = mine.Max(n => n.Sector + NodeSectors);
        while (end > 64L << shift) {
          var wider = 0UL;
          for (var bit = 0; bit < 64; ++bit)
            if ((bitmap & (1UL << bit)) != 0) wider |= 1UL << (bit / 2);
          bitmap = wider;
          ++shift;
        }
        foreach (var node in mine)
          for (var bit = node.Sector >> shift; bit << shift < node.Sector + NodeSectors; ++bit)
            bitmap |= 1UL << (int)bit;
        sb[at + shiftOffset] = (byte)shift;
        BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(at + bitmapOffset), bitmap);
      }
    }
  }

  private static void StampMemberSequences(byte[] sb, ulong seq) {
    const int seqOffset = 120;
    foreach (var (type, offset, length) in Sections(sb)) {
      if (type != FieldMembersV2 || length < 16) continue;
      var memberBytes = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(offset + 8));
      if (memberBytes < seqOffset + 8) continue;
      var members = new List<int>();
      for (var at = offset + 16; at + memberBytes <= offset + length; at += memberBytes) members.Add(at);
      if (members.All(at => BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(at + seqOffset)) == 0)) continue;
      foreach (var at in members)
        BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(at + seqOffset), seq);
    }
  }

  /// <summary>Empties a member's journal and writes the one entry a clean shutdown leaves in its first bucket.</summary>
  internal static void WriteJournal(Stream image, BcacheFsAllocationBuilder.Geometry geometry, ulong magic,
      BcacheFsMetadataCommit.Published published, IReadOnlyList<BcacheFsAllocationBuilder.ReplicaSet> replicas) {
    if (geometry.JournalBuckets.Count == 0)
      throw new InvalidDataException("bcachefs: the superblock names no journal buckets.");
    foreach (var (start, count) in geometry.JournalBuckets)
      BcacheFsMetadataCommit.ZeroRange(image, start * BucketBytes, count * BucketBytes);
    var entry = BcacheFsSuperblockComposer.JournalEntry(magic, published.Roots, published.Usage, published.Inodes, replicas);
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
