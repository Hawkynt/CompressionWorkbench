#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// ZFS block pointer <c>blkptr_t</c> — 128 bytes. Layout from
/// <c>include/sys/spa.h</c>:
/// <code>
/// offset 0x00:  dva[0]  (16 bytes)  — u64 vdev_grid_asize, u64 offset
/// offset 0x10:  dva[1]  (16 bytes)
/// offset 0x20:  dva[2]  (16 bytes)
/// offset 0x30:  prop    (8 bytes)   — lsize/psize/compression/checksum/type/level/etc.
/// offset 0x38:  pad[2]  (16 bytes)
/// offset 0x48:  physical-birth (8 bytes)
/// offset 0x50:  birth    (8 bytes)
/// offset 0x58:  fill     (8 bytes)
/// offset 0x60:  checksum (32 bytes: 4 × u64)  — ends at 0x80 = 128
/// </code>
/// DVA layout: <c>vdev (32 bits) | GRID (8 bits) | ASIZE (24 bits)</c> packed as one u64
/// (upper word), plus u64 offset (lower word) whose top bit is <c>G</c> (gang flag).
/// </summary>
internal static class BlockPointer {
  public const int Size = 128;

  public sealed class Builder {
    public uint Vdev { get; set; } = 0;
    public byte Grid { get; set; } = 0;
    /// <summary>Allocated size in sectors (ashift units), minus 1. So 1 sector = 0.</summary>
    public uint AsizeSectors { get; set; } = 0;
    /// <summary>Byte offset within vdev, divided by 2^ashift (i.e. sector index), minus DVA_OFFSET_SHIFT padding (we use raw sector offset + 4MB boot reserve).</summary>
    public ulong OffsetSectors { get; set; } = 0;
    public uint Lsize { get; set; } = 0; // logical sectors - 1
    public uint Psize { get; set; } = 0; // physical sectors - 1
    public byte Compression { get; set; } = ZfsConstants.ZioCompressOff;
    public byte Checksum { get; set; } = ZfsConstants.ZioChecksumFletcher4;
    public byte Type { get; set; } = 0;
    public byte Level { get; set; } = 0;
    public ulong Birth { get; set; } = 0;
    public ulong Fill { get; set; } = 1;
    public Fletcher4.Value Cksum { get; set; }
    public bool Embedded { get; set; } = false;
    public bool Encrypted { get; set; } = false;
    public bool ByteOrder { get; set; } = true; // true = LE (native)
  }

  public static void Write(Span<byte> dest, Builder b) {
    if (dest.Length < Size) throw new ArgumentException("Destination too small.", nameof(dest));
    dest[..Size].Clear();

    // DVA[0] — only populated, DVA[1..2] left zero.
    // word0 = ((asize & 0x00FFFFFF) | (grid << 24)) | ((u64)vdev << 32)
    ulong dvaWord0 = ((ulong)b.AsizeSectors & 0x00FFFFFF) | ((ulong)b.Grid << 24) | ((ulong)b.Vdev << 32);
    BinaryPrimitives.WriteUInt64LittleEndian(dest[..8], dvaWord0);
    // word1 = offset | (gang << 63). No gang.
    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(8, 8), b.OffsetSectors);

    // prop @ 0x30:
    // bits  0..15 = lsize - 1 (in sectors)
    // bits 16..31 = psize - 1
    // bits 32..39 = compression
    // bits 40..47 = checksum
    // bits 48..55 = type
    // bits 56..60 = level
    // bit      61 = encrypted
    // bit      62 = dedup
    // bit      63 = byteorder (1 = LE)
    ulong prop =
      ((ulong)(b.Lsize & 0xFFFF)) |
      ((ulong)(b.Psize & 0xFFFF) << 16) |
      ((ulong)b.Compression << 32) |
      ((ulong)b.Checksum << 40) |
      ((ulong)b.Type << 48) |
      ((ulong)(b.Level & 0x1F) << 56) |
      (b.Embedded ? 1UL << 39 : 0) |
      (b.Encrypted ? 1UL << 61 : 0) |
      (b.ByteOrder ? 1UL << 63 : 0);
    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(0x30, 8), prop);

    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(0x48, 8), b.Birth); // physical birth
    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(0x50, 8), b.Birth); // logical birth
    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(0x58, 8), b.Fill);

    // Checksum at 0x60, 32 bytes (4 × u64 LE) — ends at 0x80 = 128.
    b.Cksum.WriteLe(dest.Slice(0x60, 32));
  }

  public static Builder Read(ReadOnlySpan<byte> src) {
    if (src.Length < Size) throw new ArgumentException("Source too small.", nameof(src));
    var dvaWord0 = BinaryPrimitives.ReadUInt64LittleEndian(src[..8]);
    var dvaWord1 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(8, 8));
    var prop = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0x30, 8));
    var b = new Builder {
      AsizeSectors = (uint)(dvaWord0 & 0x00FFFFFF),
      Grid = (byte)((dvaWord0 >> 24) & 0xFF),
      Vdev = (uint)((dvaWord0 >> 32) & 0xFFFFFFFF),
      OffsetSectors = dvaWord1 & 0x7FFFFFFFFFFFFFFFUL,
      Lsize = (uint)(prop & 0xFFFF),
      Psize = (uint)((prop >> 16) & 0xFFFF),
      Compression = (byte)((prop >> 32) & 0x7F),
      Checksum = (byte)((prop >> 40) & 0xFF),
      Type = (byte)((prop >> 48) & 0xFF),
      Level = (byte)((prop >> 56) & 0x1F),
      Birth = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0x50, 8)),
      Fill = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0x58, 8)),
      Cksum = Fletcher4.Value.ReadLe(src.Slice(0x60, 32)),
      ByteOrder = ((prop >> 63) & 1) != 0,
      Encrypted = ((prop >> 61) & 1) != 0,
      Embedded = ((prop >> 39) & 1) != 0,
    };
    return b;
  }
}

/// <summary>
/// ZFS dnode <c>dnode_phys_t</c> — 512 bytes. Layout from <c>include/sys/dnode.h</c>:
/// <code>
/// 0x000  u8  dn_type
/// 0x001  u8  dn_indblkshift
/// 0x002  u8  dn_nlevels
/// 0x003  u8  dn_nblkptr          — number of blkptrs (usually 1 for small files, up to 3)
/// 0x004  u8  dn_bonustype
/// 0x005  u8  dn_checksum
/// 0x006  u8  dn_compress
/// 0x007  u8  dn_flags
/// 0x008  u32 dn_datablkszsec     — block size / 512
/// 0x00C  u16 dn_bonuslen
/// 0x00E  u8  dn_extra_slots
/// 0x00F  u8  dn_pad
/// 0x010  u64 dn_maxblkid
/// 0x018  u64 dn_used             — bytes / sectors used (flag bit)
/// 0x020  u64 dn_pad2[4]
/// 0x040  blkptr_t dn_blkptr[nblkptr]   — typically 1 (128 B)
/// 0x0C0  u8  dn_bonus[...]             — remainder of 512 for bonus
/// </code>
/// </summary>
internal static class Dnode {
  public const int Size = 512;
  public const int BlkPtrOffset = 0x40;
  public const int BonusOffset = 0xC0;
  public const int BonusCap = Size - BonusOffset;   // 320 bytes max bonus

  public sealed class Builder {
    public byte Type { get; set; }
    public byte IndirectBlockShift { get; set; } = 14;
    public byte Levels { get; set; } = 1;
    public byte NumBlkPtr { get; set; } = 1;
    public byte BonusType { get; set; } = 0;
    public byte Checksum { get; set; } = ZfsConstants.ZioChecksumFletcher4;
    public byte Compress { get; set; } = ZfsConstants.ZioCompressOff;
    public byte Flags { get; set; } = ZfsConstants.DnodeFlagUsedBytes;
    public uint DataBlockSizeInSectors { get; set; } = 1;  // block size / 512
    public ushort BonusLen { get; set; } = 0;
    public ulong MaxBlockId { get; set; } = 0;
    public ulong UsedBytes { get; set; } = 0;
    public BlockPointer.Builder? BlkPtr0 { get; set; }
    public byte[]? Bonus { get; set; }
  }

  public static void Write(Span<byte> dest, Builder b) {
    if (dest.Length < Size) throw new ArgumentException("Destination too small.", nameof(dest));
    dest[..Size].Clear();

    dest[0x00] = b.Type;
    dest[0x01] = b.IndirectBlockShift;
    dest[0x02] = b.Levels;
    dest[0x03] = b.NumBlkPtr;
    dest[0x04] = b.BonusType;
    dest[0x05] = b.Checksum;
    dest[0x06] = b.Compress;
    dest[0x07] = b.Flags;
    BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(0x08, 4), b.DataBlockSizeInSectors);
    BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(0x0C, 2), b.BonusLen);
    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(0x10, 8), b.MaxBlockId);
    BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(0x18, 8), b.UsedBytes);

    if (b.BlkPtr0 != null)
      BlockPointer.Write(dest.Slice(BlkPtrOffset, BlockPointer.Size), b.BlkPtr0);

    if (b.Bonus != null) {
      var len = Math.Min(b.Bonus.Length, BonusCap);
      b.Bonus.AsSpan(0, len).CopyTo(dest.Slice(BonusOffset, len));
    }
  }

  public static Builder Read(ReadOnlySpan<byte> src) {
    if (src.Length < Size) throw new ArgumentException("Source too small.", nameof(src));
    var b = new Builder {
      Type = src[0x00],
      IndirectBlockShift = src[0x01],
      Levels = src[0x02],
      NumBlkPtr = src[0x03],
      BonusType = src[0x04],
      Checksum = src[0x05],
      Compress = src[0x06],
      Flags = src[0x07],
      DataBlockSizeInSectors = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0x08, 4)),
      BonusLen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(0x0C, 2)),
      MaxBlockId = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0x10, 8)),
      UsedBytes = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0x18, 8)),
    };
    if (b.NumBlkPtr > 0)
      b.BlkPtr0 = BlockPointer.Read(src.Slice(BlkPtrOffset, BlockPointer.Size));
    if (b.BonusLen > 0)
      b.Bonus = src.Slice(BonusOffset, Math.Min((int)b.BonusLen, BonusCap)).ToArray();
    return b;
  }
}

/// <summary>
/// ZFS uberblock <c>uberblock_t</c> (sized to the uberblock slot, which is 1 KB for pool
/// version &lt;= 28 at ashift 9). Layout from <c>include/sys/uberblock_impl.h</c>:
/// <code>
/// 0x00 u64 ub_magic
/// 0x08 u64 ub_version
/// 0x10 u64 ub_txg
/// 0x18 u64 ub_guid_sum
/// 0x20 u64 ub_timestamp
/// 0x28 blkptr_t ub_rootbp  (128 B)
/// 0xA8 u64 ub_software_version
/// 0xB0 ...   padding to slot size
/// </code>
/// </summary>
internal static class Uberblock {
  public const int MagicOffset = 0x00;
  public const int VersionOffset = 0x08;
  public const int TxgOffset = 0x10;
  public const int GuidSumOffset = 0x18;
  public const int TimestampOffset = 0x20;
  public const int RootBpOffset = 0x28;
  public const int SoftwareVersionOffset = 0x28 + BlockPointer.Size; // 0xA8

  public sealed class Builder {
    public ulong Version { get; set; } = ZfsConstants.PoolVersion;
    public ulong Txg { get; set; } = 4;
    public ulong GuidSum { get; set; }
    public ulong Timestamp { get; set; }
    public BlockPointer.Builder RootBp { get; set; } = new();
    public ulong SoftwareVersion { get; set; } = ZfsConstants.PoolVersion;
  }

  public static void Write(Span<byte> slot, Builder b) {
    slot.Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(slot[..8], ZfsConstants.UberblockMagic);
    BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(VersionOffset, 8), b.Version);
    BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(TxgOffset, 8), b.Txg);
    BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(GuidSumOffset, 8), b.GuidSum);
    BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(TimestampOffset, 8), b.Timestamp);
    BlockPointer.Write(slot.Slice(RootBpOffset, BlockPointer.Size), b.RootBp);
    BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(SoftwareVersionOffset, 8), b.SoftwareVersion);
  }

  public static Builder Read(ReadOnlySpan<byte> slot) {
    if (BinaryPrimitives.ReadUInt64LittleEndian(slot[..8]) != ZfsConstants.UberblockMagic)
      throw new InvalidDataException("Uberblock magic mismatch.");
    return new Builder {
      Version = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(VersionOffset, 8)),
      Txg = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(TxgOffset, 8)),
      GuidSum = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(GuidSumOffset, 8)),
      Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(TimestampOffset, 8)),
      RootBp = BlockPointer.Read(slot.Slice(RootBpOffset, BlockPointer.Size)),
      SoftwareVersion = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(SoftwareVersionOffset, 8)),
    };
  }
}

/// <summary>
/// ZFS objset physical header <c>objset_phys_t</c> — 1024 bytes. Contains the meta-dnode
/// (which describes the array of dnodes for this objset) plus objset type and metadata.
/// Layout from <c>include/sys/dmu_objset.h</c>:
/// <code>
/// 0x000  dnode_phys_t os_meta_dnode  (512 bytes)
/// 0x200  zil_header_t os_zil_header  (192 bytes) — zeros OK for WORM
/// 0x2C0  u64 os_type
/// 0x2C8  u64 os_flags
/// 0x2D0  u8  os_portable_mac[16]
/// 0x2E0  u8  os_local_mac[16]
/// 0x2F0  ...  padding
/// </code>
/// </summary>
internal static class ObjsetPhys {
  public const int Size = 1024;
  public const int MetaDnodeOffset = 0;
  public const int ZilHeaderOffset = 0x200;
  public const int TypeOffset = 0x2C0;
  public const int FlagsOffset = 0x2C8;

  public static void Write(Span<byte> block, Dnode.Builder metaDnode, byte osType) {
    if (block.Length < Size) throw new ArgumentException("Block too small.", nameof(block));
    block[..Size].Clear();
    Dnode.Write(block.Slice(MetaDnodeOffset, Dnode.Size), metaDnode);
    BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(TypeOffset, 8), osType);
    // Flags = 0 for our purposes.
  }

  public static (Dnode.Builder MetaDnode, byte OsType) Read(ReadOnlySpan<byte> block) {
    var meta = Dnode.Read(block.Slice(MetaDnodeOffset, Dnode.Size));
    var osType = (byte)BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(TypeOffset, 8));
    return (meta, osType);
  }
}

/// <summary>
/// DSL directory physical record <c>dsl_dir_phys_t</c>, stored in dnode bonus area.
/// 256 bytes. Most fields can be zero for our WORM pool. Only the <c>head_dataset_obj</c>
/// field actually matters — it points to the DSL dataset dnode.
/// </summary>
internal static class DslDirPhys {
  public const int Size = 256;

  public sealed class Builder {
    public ulong CreationTime { get; set; }
    public ulong HeadDatasetObj { get; set; }
    public ulong ParentObj { get; set; }
    public ulong UsedBytes { get; set; }
    public ulong CompressedBytes { get; set; }
    public ulong UncompressedBytes { get; set; }
    public ulong Quota { get; set; }
    public ulong Reserved { get; set; }
    public ulong PropsZapObj { get; set; }
    public ulong DeletedDataset { get; set; }
  }

  public static byte[] Encode(Builder b) {
    var bytes = new byte[Size];
    var s = bytes.AsSpan();
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0, 8), b.CreationTime);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(8, 8), b.HeadDatasetObj);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(16, 8), b.ParentObj);
    // origin_obj at 24 — leave 0
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(32, 8), b.PropsZapObj);
    // child_dir_zapobj at 40 — leave 0
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(48, 8), b.UsedBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(56, 8), b.CompressedBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(64, 8), b.UncompressedBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(72, 8), b.Quota);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(80, 8), b.Reserved);
    return bytes;
  }

  public static Builder Decode(ReadOnlySpan<byte> s) {
    return new Builder {
      CreationTime = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(0, 8)),
      HeadDatasetObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8, 8)),
      ParentObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(16, 8)),
      PropsZapObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(32, 8)),
      UsedBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(48, 8)),
      CompressedBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(56, 8)),
      UncompressedBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(64, 8)),
      Quota = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(72, 8)),
      Reserved = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(80, 8)),
    };
  }
}

/// <summary>
/// DSL dataset physical record <c>dsl_dataset_phys_t</c>, stored in dnode bonus area.
/// 320 bytes. Holds the blkptr of the dataset's <c>objset_phys_t</c>.
/// </summary>
internal static class DslDatasetPhys {
  public const int Size = 320;
  public const int BpOffset = 128; // ds_bp starts after header fields

  public sealed class Builder {
    public ulong DirObj { get; set; }
    public ulong PrevSnapObj { get; set; }
    public ulong PrevSnapTxg { get; set; }
    public ulong NextSnapObj { get; set; }
    public ulong SnapNamesZapObj { get; set; }
    public ulong NumChildren { get; set; }
    public ulong CreationTime { get; set; }
    public ulong CreationTxg { get; set; }
    public ulong DeadlistObj { get; set; }
    public ulong UsedBytes { get; set; }
    public ulong CompressedBytes { get; set; }
    public ulong UncompressedBytes { get; set; }
    public ulong UniqueBytes { get; set; }
    public ulong FsidGuid { get; set; }
    public ulong Guid { get; set; }
    public ulong Flags { get; set; }
    public BlockPointer.Builder Bp { get; set; } = new();
  }

  public static byte[] Encode(Builder b) {
    var bytes = new byte[Size];
    var s = bytes.AsSpan();
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0, 8), b.DirObj);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(8, 8), b.PrevSnapObj);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(16, 8), b.PrevSnapTxg);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(24, 8), b.NextSnapObj);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(32, 8), b.SnapNamesZapObj);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(40, 8), b.NumChildren);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(48, 8), b.CreationTime);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(56, 8), b.CreationTxg);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(64, 8), b.DeadlistObj);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(72, 8), b.UsedBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(80, 8), b.CompressedBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(88, 8), b.UncompressedBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(96, 8), b.UniqueBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(104, 8), b.FsidGuid);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(112, 8), b.Guid);
    BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(120, 8), b.Flags);
    BlockPointer.Write(s.Slice(BpOffset, BlockPointer.Size), b.Bp);
    // remaining fields (next_clones_obj, props_obj, userrefs_obj) left zero.
    return bytes;
  }

  public static Builder Decode(ReadOnlySpan<byte> s) {
    var b = new Builder {
      DirObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(0, 8)),
      PrevSnapObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8, 8)),
      PrevSnapTxg = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(16, 8)),
      NextSnapObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(24, 8)),
      SnapNamesZapObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(32, 8)),
      NumChildren = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(40, 8)),
      CreationTime = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(48, 8)),
      CreationTxg = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(56, 8)),
      DeadlistObj = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(64, 8)),
      UsedBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(72, 8)),
      CompressedBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(80, 8)),
      UncompressedBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(88, 8)),
      UniqueBytes = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(96, 8)),
      FsidGuid = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(104, 8)),
      Guid = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(112, 8)),
      Flags = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(120, 8)),
      Bp = BlockPointer.Read(s.Slice(BpOffset, BlockPointer.Size)),
    };
    return b;
  }
}
