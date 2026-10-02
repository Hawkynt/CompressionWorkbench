#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Lays out the superblock of a single-device volume at metadata version 1.3:
/// the fixed header, the options words, the layout, and the variable sections a
/// formatter writes — members, replicas, journal, clean and errors.
/// </summary>
/// <remarks>
/// Every value here was set against a superblock <c>bcachefs format</c> wrote and
/// then shut down cleanly; <c>docs/BCACHEFS-ON-DISK.md</c> lists them.
/// </remarks>
internal static class BcacheFsSuperblockComposer {

  /// <summary>Everything the superblock says that the trees do not.</summary>
  internal sealed record Description(
    Guid InternalUuid,
    Guid UserUuid,
    Guid MemberUuid,
    string Label,
    long DeviceSectors,
    IReadOnlyList<long> SuperblockSectors,
    IReadOnlyList<(long Start, long Count)> JournalBuckets,
    ulong Sequence = 1);

  /// <summary>A b-tree root as the clean section records it.</summary>
  internal readonly record struct Root(int Btree, int Level, Key Pointer);

  /// <summary>Builds a whole superblock; the caller stamps the slot offset and the checksum.</summary>
  internal static byte[] Build(Description description, IReadOnlyList<Root> roots,
      BcacheFsAllocationBuilder.Usage usage, ulong inodes) {
    ArgumentNullException.ThrowIfNull(description);
    var sections = new List<byte[]> {
      MembersV2Section(description),
      MembersV1Section(description),
      ReplicasV0Section(),
      JournalSection(description.JournalBuckets),
      CleanSection(roots, usage, inodes),
      ErrorsSection(),
    };

    var variable = sections.Sum(s => s.Length);
    var sb = new byte[SbFixedBytes + variable];
    var span = sb.AsSpan();

    BinaryPrimitives.WriteUInt16LittleEndian(span[16..], BcacheFsFormat.Version);
    BinaryPrimitives.WriteUInt16LittleEndian(span[18..], VersionMin);
    Magic.CopyTo(span[24..]);
    WriteGuid(span[40..], description.InternalUuid);
    WriteGuid(span[56..], description.UserUuid);
    var label = Encoding.UTF8.GetBytes(description.Label);
    label.AsSpan(0, Math.Min(32, label.Length)).CopyTo(span[72..]);
    BinaryPrimitives.WriteUInt64LittleEndian(span[112..], description.Sequence);
    BinaryPrimitives.WriteUInt16LittleEndian(span[120..], 1);                  // block_size, in sectors
    sb[122] = 0;                                                               // dev_idx
    sb[123] = 1;                                                               // nr_devices
    BinaryPrimitives.WriteUInt32LittleEndian(span[124..], (uint)(variable / 8));
    BinaryPrimitives.WriteUInt32LittleEndian(span[140..], 1);                  // time_precision

    WriteFlags(span[144..208]);
    BinaryPrimitives.WriteUInt64LittleEndian(span[208..], Features);
    BinaryPrimitives.WriteUInt64LittleEndian(span[224..], CompatFeatures);

    var layout = span[SbLayoutOffset..];
    Magic.CopyTo(layout);
    layout[16] = 0;                                                            // layout_type
    layout[17] = SbMaxSizeBits;
    layout[18] = (byte)description.SuperblockSectors.Count;
    for (var i = 0; i < description.SuperblockSectors.Count; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(layout[(24 + 8 * i)..], (ulong)description.SuperblockSectors[i]);

    var cursor = SbFixedBytes;
    foreach (var section in sections) {
      section.CopyTo(sb, cursor);
      cursor += section.Length;
    }

    return sb;
  }

  /// <summary>
  /// Replaces the clean section of an existing superblock with one naming
  /// <paramref name="roots" /> and carrying <paramref name="usage" />.
  /// </summary>
  internal static byte[] ReplaceClean(byte[] superblock, IReadOnlyList<Root> roots,
      BcacheFsAllocationBuilder.Usage usage, ulong inodes) {
    var clean = CleanSection(roots, usage, inodes);
    var before = new List<byte[]>();
    var offset = SbFixedBytes;
    var u64s = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(124));
    var end = SbFixedBytes + u64s * 8;
    var replaced = false;
    while (offset + 8 <= end) {
      var words = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(offset));
      if (words == 0) break;
      var type = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(offset + 4));
      if (type == FieldClean) {
        before.Add(clean);
        replaced = true;
      } else
        before.Add(superblock.AsSpan(offset, words * 8).ToArray());
      offset += words * 8;
    }
    if (!replaced) before.Add(clean);

    var variable = before.Sum(s => s.Length);
    var result = new byte[SbFixedBytes + variable];
    superblock.AsSpan(0, SbFixedBytes).CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(124), (uint)(variable / 8));
    var at = SbFixedBytes;
    foreach (var s in before) {
      s.CopyTo(result, at);
      at += s.Length;
    }
    return result;
  }

  /// <summary>Stamps the superblock's own checksum over everything after it.</summary>
  internal static void StampChecksum(byte[] superblock) {
    var checksum = MetadataChecksum(superblock.AsSpan(16));
    BinaryPrimitives.WriteUInt64LittleEndian(superblock, checksum);
    BinaryPrimitives.WriteUInt64LittleEndian(superblock.AsSpan(8), 0);
  }

  /// <summary>The volume options, as the words after the time fields hold them.</summary>
  private static void WriteFlags(Span<byte> flags) {
    var words = new ulong[8];

    void Set(int word, int lo, int hi, ulong value)
      => words[word] |= (value & ((1UL << (hi - lo)) - 1)) << lo;

    Set(0, 0, 1, 1);                     // initialized
    Set(0, 1, 2, 1);                     // clean
    Set(0, 2, 8, CsumTypeCrc32CNonzero); // superblock checksum
    Set(0, 8, 12, 1);                    // on error: go read-only
    Set(0, 12, 28, BucketSectors);       // btree node size, in sectors
    Set(0, 28, 33, 8);                   // gc reserve, per cent
    // These name a choice, not a function: "crc32c" is one option, and which of
    // the two crc32c variants it becomes depends on what is being summed.
    Set(0, 40, 44, ChecksumOptionCrc32C);
    Set(0, 44, 48, ChecksumOptionCrc32C);
    Set(0, 48, 52, 1);                   // metadata replicas wanted
    Set(0, 52, 56, 1);                   // data replicas wanted
    Set(0, 56, 57, 1);                   // POSIX ACLs

    Set(1, 0, 4, StrHashSiphashOption);
    Set(1, 8, 9, 1);                     // 32-bit inode numbers
    Set(1, 14, 20, EncodedExtentMaxBits);
    Set(1, 20, 24, 1);                   // metadata replicas required
    Set(1, 24, 28, 1);                   // data replicas required

    Set(3, 28, 29, 1);                   // shard inode numbers
    Set(3, 29, 30, 1);                   // inodes use the key cache
    Set(3, 30, 62, 1000);                // journal flush delay, ms
    Set(4, 0, 32, 100);                  // journal reclaim delay, ms
    Set(4, 32, 33, 1);                   // journal transaction names

    Set(5, 0, 16, BcacheFsFormat.Version);              // version upgrade complete

    for (var i = 0; i < words.Length; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(flags[(8 * i)..], words[i]);
  }

  private static byte[] Section(uint type, int payloadBytes) {
    var section = new byte[8 + (payloadBytes + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt32LittleEndian(section, (uint)(section.Length / 8));
    BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), type);
    return section;
  }

  /// <summary>The fields a <c>bch_member</c> shares between its two encodings.</summary>
  private static void WriteMember(Span<byte> member, Description d) {
    WriteGuid(member, d.MemberUuid);
    BinaryPrimitives.WriteUInt64LittleEndian(member[16..], (ulong)(d.DeviceSectors / BucketSectors));
    BinaryPrimitives.WriteUInt16LittleEndian(member[24..], 0);                 // first_bucket
    BinaryPrimitives.WriteUInt16LittleEndian(member[26..], BucketSectors);
    // Read-write, journal/btree/user allowed, durability one — stored one higher
    // so that zero can mean the default — and freespace initialised, without which
    // the checker skips the freespace tree and a mount stops to build it.
    var flags = (28UL << 15) | (2UL << 28) | (1UL << 30);
    BinaryPrimitives.WriteUInt64LittleEndian(member[40..], flags);
  }

  private static byte[] MembersV2Section(Description d) {
    var section = Section(FieldMembersV2, 8 + MemberBytes);
    BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(8), MemberBytes);
    WriteMember(section.AsSpan(16), d);
    return section;
  }

  /// <summary>The older, shorter copy of the members list, which a formatter still writes.</summary>
  private static byte[] MembersV1Section(Description d) {
    var section = Section(FieldMembersV1, MemberV1Bytes);
    WriteMember(section.AsSpan(8), d);
    return section;
  }

  /// <summary>The replicas entries the volume's content can name, in the order a formatter keeps them.</summary>
  private static readonly byte[] ReplicaTypes = [DataBtree, DataJournal, DataUser];

  /// <summary>
  /// Which sets of devices hold a copy of what. A usage total may only name a set
  /// declared here; the v0 entry is the content type, how many devices, and which.
  /// </summary>
  private static byte[] ReplicasV0Section() {
    var section = Section(FieldReplicasV0, 3 * ReplicaTypes.Length);
    for (var i = 0; i < ReplicaTypes.Length; ++i) {
      section[8 + 3 * i] = ReplicaTypes[i];
      section[9 + 3 * i] = 1;                                                  // nr_devs
      section[10 + 3 * i] = 0;                                                 // the one device
    }
    return section;
  }

  private static byte[] JournalSection(IReadOnlyList<(long Start, long Count)> journal) {
    var section = Section(FieldJournalV2, 16 * journal.Count);
    for (var i = 0; i < journal.Count; ++i) {
      BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(8 + 16 * i), (ulong)journal[i].Start);
      BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(16 + 16 * i), (ulong)journal[i].Count);
    }
    return section;
  }

  private static byte[] Entry(int u64s, byte type, byte btree = 0, byte level = 0) {
    var entry = new byte[8 + u64s * 8];
    BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)u64s);
    entry[2] = btree;
    entry[3] = level;
    entry[4] = type;
    return entry;
  }

  /// <summary>
  /// The state a clean shutdown leaves behind: the usage totals, the clocks, and
  /// every b-tree root, each framed as a journal entry.
  /// </summary>
  /// <remarks>
  /// The same list goes into the clean section and into the one journal entry the
  /// volume carries; a checker compares the two root by root, byte for byte.
  /// </remarks>
  private static List<byte[]> ShutdownEntries(IReadOnlyList<Root> roots, BcacheFsAllocationBuilder.Usage usage, ulong inodes) {
    var entries = new List<byte[]>();

    byte[] FsUsage(byte kind, byte level, ulong v) {
      var e = Entry(1, JsetUsage, kind, level);
      BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(8), v);
      return e;
    }

    entries.Add(FsUsage(FsUsageInodes, 0, inodes));
    entries.Add(FsUsage(FsUsageKeyVersion, 0, 0));
    for (byte replicas = 0; replicas < 4; ++replicas)
      entries.Add(FsUsage(FsUsageReserved, replicas, 0));

    foreach (var dataType in ReplicaTypes) {
      var sectors = dataType switch {
        DataBtree => usage.BtreeSectors,
        DataUser => usage.UserSectors,
        _ => 0UL,
      };
      var e = Entry(2, JsetDataUsage);
      BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(8), sectors);
      e[16] = dataType;
      e[17] = 1;                                                               // nr_devs
      e[18] = 1;                                                               // nr_required
      e[19] = 0;                                                               // the one device
      entries.Add(e);
    }

    var dev = Entry(4 + 3 * DataTypeCount - 1, JsetDevUsage);
    // dev, pad, buckets_ec and the retired buckets_unavailable, then one triple per type.
    for (var t = 0; t < DataTypeCount; ++t) {
      var at = 8 + 24 + 24 * t;
      BinaryPrimitives.WriteUInt64LittleEndian(dev.AsSpan(at), usage.Buckets[t]);
      BinaryPrimitives.WriteUInt64LittleEndian(dev.AsSpan(at + 8), usage.Sectors[t]);
      BinaryPrimitives.WriteUInt64LittleEndian(dev.AsSpan(at + 16), usage.Fragmented[t]);
    }
    entries.Add(dev);

    for (byte clock = 0; clock < 2; ++clock) {
      var e = Entry(2, JsetClock);
      e[8] = clock;                                                            // read, then write
      entries.Add(e);
    }

    foreach (var root in roots) {
      var e = Entry(root.Pointer.Bytes / 8, JsetBtreeRoot, (byte)root.Btree, (byte)root.Level);
      WriteKey(e.AsSpan(8), root.Pointer);
      entries.Add(e);
    }

    return entries;
  }

  private static byte[] CleanSection(IReadOnlyList<Root> roots, BcacheFsAllocationBuilder.Usage usage, ulong inodes) {
    var entries = ShutdownEntries(roots, usage, inodes);

    // flags, the two retired clock fields, and the journal sequence come first.
    var payload = 16 + entries.Sum(e => e.Length);
    var section = Section(FieldClean, payload);
    BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(16), CleanJournalSeq);
    var cursor = 24;
    foreach (var e in entries) {
      e.CopyTo(section, cursor);
      cursor += e.Length;
    }
    return section;
  }

  /// <summary>
  /// The one journal entry a cleanly shut down volume of this profile carries:
  /// the last flush before shutdown, holding no keys, only the roots and totals.
  /// </summary>
  /// <remarks>
  /// <para>A version 1.3 reader recovering a clean volume could do without it and
  /// take the clean section alone, but a newer one upgrading the volume replays
  /// the journal instead, and finds the roots there or not at all — which is why
  /// <c>bcachefs format</c> leaves its last journal entry behind as well.</para>
  ///
  /// <para>It is <c>struct jset</c>: checksum, magic, sequence, version, flags,
  /// length, two retired clocks and the oldest sequence still needed — this one,
  /// since nothing before it is — then the entries, padded out to whole sectors.
  /// The checksum covers everything from the magic on.</para>
  /// </remarks>
  internal static byte[] JournalEntry(ulong superblockMagic, IReadOnlyList<Root> roots,
      BcacheFsAllocationBuilder.Usage usage, ulong inodes) {
    var entries = ShutdownEntries(roots, usage, inodes);
    var payload = entries.Sum(e => e.Length);
    const int header = 56;
    var jset = new byte[(header + payload + SectorSize - 1) / SectorSize * SectorSize];
    BinaryPrimitives.WriteUInt64LittleEndian(jset.AsSpan(16), superblockMagic ^ JsetMagic);
    BinaryPrimitives.WriteUInt64LittleEndian(jset.AsSpan(24), CleanJournalSeq);
    BinaryPrimitives.WriteUInt32LittleEndian(jset.AsSpan(32), BcacheFsFormat.Version);
    BinaryPrimitives.WriteUInt32LittleEndian(jset.AsSpan(36), CsumTypeCrc32CNonzero);  // flags: checksum type, a flush
    BinaryPrimitives.WriteUInt32LittleEndian(jset.AsSpan(40), (uint)(payload / 8));
    BinaryPrimitives.WriteUInt64LittleEndian(jset.AsSpan(48), CleanJournalSeq);         // last_seq
    var cursor = header;
    foreach (var e in entries) {
      e.CopyTo(jset, cursor);
      cursor += e.Length;
    }
    BinaryPrimitives.WriteUInt64LittleEndian(jset, MetadataChecksum(jset.AsSpan(16, header - 16 + payload)));
    return jset;
  }

  private static byte[] ErrorsSection() => Section(FieldErrors, 0);

  private static void WriteGuid(Span<byte> destination, Guid value) {
    Span<byte> bytes = stackalloc byte[16];
    value.TryWriteBytes(bytes);
    bytes.CopyTo(destination);
  }
}
