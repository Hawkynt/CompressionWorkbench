using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// Turns a block a big-endian host wrote into the byte order the rest of the reader parses: what
/// OpenZFS does to a block whose pointer says it was written in the other byte order
/// (<c>docs/ZFS-ON-DISK.md</c>, "Big-endian pools"). Every structure is swapped field by field —
/// 64-bit words, ZAP leaves and micro-ZAP entries, dnodes with their bonus buffers, object sets
/// and old-style znodes — chosen by the object type the block pointer names.
/// </summary>
/// <remarks>
/// System-attribute containers (bonus buffers and spill blocks of type SA) are left as stored:
/// their header says which order they are in, and <see cref="ZfsSystemAttributes" /> swaps each
/// attribute by the kind its registration names. ACL entries are not swapped; the reader does not
/// interpret them.
/// </remarks>
internal static class ZfsByteSwap {
  private enum Kind : byte { UInt8, UInt16, UInt32, UInt64, Zap, Dnode, Objset, Znode, OldAcl, Acl }

  // dmu_ot[]: the byte-swap kind of each legacy object type (0 … 53), copied from OpenZFS dmu.c
  private static readonly Kind[] LegacyKinds = [
    Kind.UInt8, Kind.Zap, Kind.UInt64, Kind.UInt8, Kind.UInt64, Kind.UInt64, Kind.UInt64, Kind.UInt64, Kind.UInt64, Kind.UInt64,
    Kind.Dnode, Kind.Objset, Kind.UInt64, Kind.Zap, Kind.Zap, Kind.Zap, Kind.UInt64, Kind.Znode, Kind.OldAcl, Kind.UInt8,
    Kind.Zap, Kind.Zap, Kind.Zap, Kind.UInt8, Kind.Zap, Kind.UInt8, Kind.UInt64, Kind.Zap, Kind.Zap, Kind.UInt8,
    Kind.UInt64, Kind.Zap, Kind.Zap, Kind.Acl, Kind.UInt8, Kind.UInt8, Kind.UInt64, Kind.Zap, Kind.Zap, Kind.Zap,
    Kind.Zap, Kind.Zap, Kind.Zap, Kind.Zap, Kind.UInt8, Kind.Zap, Kind.Zap, Kind.Zap, Kind.Zap, Kind.UInt8,
    Kind.Zap, Kind.UInt64, Kind.Zap, Kind.UInt64,
  ];

  private const int DnodeSize = 512, BlockPointerSize = 128;
  private const ulong ZapBlockMicro = (1UL << 63) + 3, ZapBlockLeaf = (1UL << 63) + 0;
  private const byte ChunkArray = 251, ChunkEntry = 252, ChunkFree = 253;

  /// <summary>The byte-swap kind of an object type: legacy types by table, new-style ones (bit 7) carry it in their low five bits.</summary>
  private static Kind KindOf(byte type) {
    if ((type & 0x80) != 0) {
      var index = type & 0x1F;
      return index <= (int)Kind.Acl ? (Kind)index : throw new InvalidDataException($"ZFS: object type 0x{type:X2} names byte-swap kind {index}.");
    }
    return type < LegacyKinds.Length ? LegacyKinds[type] : throw new InvalidDataException($"ZFS: object type {type} is unknown.");
  }

  /// <summary>Swaps a logical block of <paramref name="type" /> at <paramref name="level" /> in place.</summary>
  public static void Block(byte type, int level, Span<byte> block) {
    if (level > 0) {
      UInt64Array(block); // indirect blocks are block pointers
      return;
    }
    Apply(KindOf(type), block);
  }

  private static void Apply(Kind kind, Span<byte> data) {
    switch (kind) {
      case Kind.UInt8 or Kind.OldAcl or Kind.Acl: break;
      case Kind.UInt16: for (var i = 0; i + 2 <= data.Length; i += 2) Swap16(data[i..]); break;
      case Kind.UInt32: for (var i = 0; i + 4 <= data.Length; i += 4) Swap32(data[i..]); break;
      case Kind.UInt64: UInt64Array(data); break;
      case Kind.Zap: Zap(data); break;
      case Kind.Dnode: DnodeBuffer(data); break;
      case Kind.Objset: Objset(data); break;
      case Kind.Znode: Znode(data); break;
    }
  }

  public static void UInt64Array(Span<byte> data) {
    for (var i = 0; i + 8 <= data.Length; i += 8) Swap64(data[i..]);
  }

  /// <summary>A ZAP block: micro-ZAP, fat-ZAP leaf, or a header / pointer-table block (all 64-bit words).</summary>
  private static void Zap(Span<byte> data) {
    if (data.Length < 8) return;
    var blockType = BinaryPrimitives.ReadUInt64BigEndian(data);
    if (blockType == ZapBlockMicro) {
      for (var i = 0; i < 24; i += 8) Swap64(data[i..]); // block type, salt, normalisation flags
      for (var e = 64; e + 64 <= data.Length; e += 64) {
        Swap64(data[e..]);       // value
        Swap32(data[(e + 8)..]); // collision differentiator
      }
    } else if (blockType == ZapBlockLeaf)
      ZapLeaf(data);
    else
      UInt64Array(data);
  }

  private static void ZapLeaf(Span<byte> data) {
    var shift = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)data.Length);
    Swap64(data);              // block type
    Swap64(data[16..]);        // prefix
    Swap32(data[24..]);        // magic
    for (var at = 28; at < 36; at += 2) Swap16(data[at..]); // free count, entries, prefix length, free list
    var hashEntries = 1 << (shift - 5);
    for (var i = 0; i < hashEntries; ++i) Swap16(data[(48 + i * 2)..]);
    var chunks = ((1 << shift) - 2 * hashEntries) / 24 - 2;
    var chunkBase = 48 + hashEntries * 2;
    for (var i = 0; i < chunks; ++i) {
      var c = data.Slice(chunkBase + i * 24, 24);
      switch (c[0]) {
        case ChunkEntry:
          for (var at = 2; at < 12; at += 2) Swap16(c[at..]); // next, name chunk, name ints, value chunk, value ints
          Swap32(c[12..]); // collision differentiator
          Swap64(c[16..]); // hash
          break;
        case ChunkFree or ChunkArray:
          Swap16(c[22..]); // next
          break;
        default:
          throw new InvalidDataException($"ZFS: big-endian ZAP leaf chunk {i} has type {c[0]}.");
      }
    }
  }

  /// <summary>A block of dnodes: each in-use dnode is swapped and its extra slots skipped.</summary>
  private static void DnodeBuffer(Span<byte> data) {
    for (var at = 0; at + DnodeSize <= data.Length;) {
      var dnode = data[at..];
      Dnode(dnode);
      at += DnodeSize;
      if (dnode[0] != 0) at += dnode[0x0C] * DnodeSize;
    }
  }

  private static void Dnode(Span<byte> d) {
    if (d[0] == 0) {
      d[..DnodeSize].Clear();
      return;
    }
    Swap16(d[0x08..]); // data block size in sectors
    Swap16(d[0x0A..]); // bonus length
    Swap64(d[0x10..]); // max block id
    Swap64(d[0x18..]); // used
    var pointers = d[0x03];
    if (pointers is < 1 or > 3) throw new InvalidDataException($"ZFS: big-endian dnode claims {pointers} block pointers.");
    var size = (d[0x0C] + 1) * DnodeSize;
    if (size > d.Length) throw new InvalidDataException("ZFS: big-endian large dnode runs past its block.");
    UInt64Array(d.Slice(0x40, pointers * BlockPointerSize));
    var spill = (d[0x07] & 4) != 0;
    var bonusAt = 0x40 + pointers * BlockPointerSize;
    var bonusEnd = spill ? size - BlockPointerSize : size;
    if (BinaryPrimitives.ReadUInt16LittleEndian(d[0x0A..]) != 0 && bonusEnd > bonusAt)
      Apply(KindOf(d[0x04]), d[bonusAt..bonusEnd]);
    if (spill) UInt64Array(d.Slice(size - BlockPointerSize, BlockPointerSize));
  }

  /// <summary>An object set: meta-dnode, intent-log header, type and flags, and the accounting dnodes when present.</summary>
  private static void Objset(Span<byte> d) {
    Dnode(d);
    UInt64Array(d.Slice(0x200, 192));
    Swap64(d[0x2C0..]);
    Swap64(d[0x2C8..]);
    if (d.Length >= 2048) {
      Dnode(d[0x400..]);
      Dnode(d[0x600..]);
      if (d.Length >= 4096) Dnode(d[0x800..]);
    }
  }

  /// <summary>An old-style znode bonus buffer: its 64-bit fields and the ACL header.</summary>
  private static void Znode(Span<byte> d) {
    if (d.Length < 0xB0 + 16) return;
    UInt64Array(d[..0xB0]);   // times, generation, mode, size, parent, links, xattr, rdev, flags, uid, gid, zap, padding
    Swap64(d[0xB0..]);        // external ACL object
    Swap32(d[0xB8..]);        // ACL size
    Swap16(d[0xBC..]);        // ACL version
    Swap16(d[0xBE..]);        // ACL count
  }

  private static void Swap16(Span<byte> p) => BinaryPrimitives.WriteUInt16LittleEndian(p, BinaryPrimitives.ReadUInt16BigEndian(p));
  private static void Swap32(Span<byte> p) => BinaryPrimitives.WriteUInt32LittleEndian(p, BinaryPrimitives.ReadUInt32BigEndian(p));
  private static void Swap64(Span<byte> p) => BinaryPrimitives.WriteUInt64LittleEndian(p, BinaryPrimitives.ReadUInt64BigEndian(p));
}
