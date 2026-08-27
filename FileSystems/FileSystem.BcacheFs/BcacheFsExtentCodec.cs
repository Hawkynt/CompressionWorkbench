#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Numerics;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Lossless parser for the variable list of extent entries carried by extents,
/// reflinks and b-tree pointers. Entry type is encoded as the index of the first
/// set bit in the first word; entry length comes from the on-disk
/// extent_type_u64s table when present, falling back to the current 1.38 sizes.
/// </summary>
internal static class BcacheFsExtentCodec {
  // sizeof(struct bch_extent_*) / sizeof(u64), metadata version 1.38.
  private static readonly byte[] CurrentEntryU64s = [
    1, // ptr
    1, // crc32
    2, // crc64
    3, // crc128
    1, // stripe_ptr
    1, // rebalance_v1
    1, // flags
    1, // reconcile
    1, // reconcile_bp
  ];

  internal static IReadOnlyList<byte> EntryU64s(BcacheFsSuperblockRecord superblock) {
    ArgumentNullException.ThrowIfNull(superblock);

    // The superblock field is primarily a forward-compatibility mechanism: it
    // may describe entry types this implementation does not know yet. For types
    // we do know, the current structure size remains authoritative just as in
    // bch2_sb_extent_type_u64s_to_cpu().
    var table = new List<byte>(CurrentEntryU64s);
    var field = superblock.FieldsOf(BcacheFsSuperblockFieldType.ExtentTypeU64s).LastOrDefault();
    if (field != null) {
      var raw = field.RawBytes;
      for (var i = 8; i < raw.Length && raw[i] != 0; ++i) {
        var type = i - 8;
        while (table.Count <= type) table.Add(0);
        if (type >= CurrentEntryU64s.Length)
          table[type] = raw[i];
      }
    }

    for (var i = 0; i < CurrentEntryU64s.Length; ++i) {
      while (table.Count <= i) table.Add(0);
      table[i] = CurrentEntryU64s[i];
    }
    return table;
  }

  internal static bool TryParseEntries(
      ReadOnlySpan<byte> value,
      BcacheFsSuperblockRecord superblock,
      out IReadOnlyList<BcacheFsExtentEntry> entries,
      out string error,
      bool? bigEndian = null) {
    ArgumentNullException.ThrowIfNull(superblock);
    var storageBigEndian = bigEndian ?? superblock.BigEndian;
    var result = new List<BcacheFsExtentEntry>();
    var sizes = EntryU64s(superblock);
    var cursor = 0;

    while (cursor < value.Length) {
      if (value.Length - cursor < sizeof(ulong)) {
        entries = result;
        error = $"extent entry at byte {cursor} has a truncated first word.";
        return false;
      }

      var firstWord = ReadNativeUInt64(value[cursor..], storageBigEndian);
      if (firstWord == 0) {
        entries = result;
        error = $"extent entry at byte {cursor} has no type bit set.";
        return false;
      }

      var rawType = BitOperations.TrailingZeroCount(firstWord);
      if (rawType >= sizes.Count || sizes[rawType] == 0) {
        entries = result;
        error = $"extent entry type {rawType} has no known on-disk length.";
        return false;
      }

      var bytes = checked(sizes[rawType] * sizeof(ulong));
      if (cursor + bytes > value.Length) {
        entries = result;
        error = $"extent entry type {rawType} at byte {cursor} overruns its value.";
        return false;
      }

      var raw = value.Slice(cursor, bytes).ToArray();
      BcacheFsExtentEntryType? known = Enum.IsDefined(typeof(BcacheFsExtentEntryType), (byte)rawType)
        ? (BcacheFsExtentEntryType)rawType
        : null;
      result.Add(new BcacheFsExtentEntry(rawType, known, raw));
      cursor += bytes;
    }

    entries = result;
    error = string.Empty;
    return true;
  }

  internal static bool TryReadBtreePointer(
      BcacheFsRawKey key,
      BcacheFsSuperblockRecord superblock,
      out BcacheFsBtreePointer? pointer,
      out string error) {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(superblock);
    pointer = null;

    var type = key.Type;
    var legacy = type == BcacheFsKeyType.BtreePtr;
    if (!legacy && type != BcacheFsKeyType.BtreePtrV2) {
      error = $"key type {key.RawType} is not a b-tree pointer.";
      return false;
    }

    var bigEndian = key.BigEndian;
    ulong sequence;
    ushort sectorsWritten;
    ushort flags;
    Bpos minKey;
    ReadOnlySpan<byte> extentBytes;

    if (legacy) {
      sequence = 0;
      // Legacy btree_ptr has no sectors_written field. Kernel
      // btree_ptr_sectors_written() deliberately returns zero so node reading
      // scans until the first mismatched bset sequence.
      sectorsWritten = 0;
      flags = 0;
      minKey = Bpos.Min;
      extentBytes = key.Value;
    } else {
      if (key.Value.Length < 40) {
        error = $"btree_ptr_v2 value is {key.Value.Length} bytes; fixed part needs 40.";
        return false;
      }
      // mem_ptr at 0..8 is an in-memory cache field and is ignored on disk.
      // seq/sectors_written/flags are explicitly little-endian in the format;
      // min_key is native-endian bpos and therefore follows the key's source.
      sequence = BinaryPrimitives.ReadUInt64LittleEndian(key.Value.AsSpan(8));
      sectorsWritten = BinaryPrimitives.ReadUInt16LittleEndian(key.Value.AsSpan(16));
      flags = BinaryPrimitives.ReadUInt16LittleEndian(key.Value.AsSpan(18));
      minKey = ReadStoredBpos(key.Value.AsSpan(20), bigEndian);
      extentBytes = key.Value.AsSpan(40);
    }

    if (!TryParseEntries(extentBytes, superblock, out var entries, out error, bigEndian))
      return false;

    var replicas = new List<BcacheFsExtentPointer>();
    foreach (var entry in entries) {
      if (entry.KnownType != BcacheFsExtentEntryType.Pointer) continue;
      var word = ReadNativeUInt64(entry.RawBytes, bigEndian);
      replicas.Add(new BcacheFsExtentPointer(
        Device: (byte)((word >> 48) & 0xFF),
        Sector: (long)((word >> 4) & ((1UL << 44) - 1)),
        Generation: (byte)(word >> 56),
        Cached: (word & (1UL << 1)) != 0,
        Unused: (word & (1UL << 2)) != 0,
        Unwritten: (word & (1UL << 3)) != 0,
        RawWord: word));
    }

    if (replicas.Count == 0) {
      error = "b-tree pointer has no physical extent pointer replicas.";
      return false;
    }

    pointer = new BcacheFsBtreePointer(
      legacy,
      sequence,
      sectorsWritten,
      flags,
      minKey,
      key.Position,
      entries,
      replicas);
    error = string.Empty;
    return true;
  }

  internal static bool TryReadExtentCrc(
      BcacheFsExtentEntry entry,
      out BcacheFsExtentCrc? crc,
      out string error) {
    crc = null;
    var bytes = entry.RawBytes;
    switch (entry.KnownType) {
      case BcacheFsExtentEntryType.Crc32: {
        var word = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        crc = new BcacheFsExtentCrc(
          CompressedSize: (uint)(((word >> 2) & 0x7F) + 1),
          UncompressedSize: (uint)(((word >> 9) & 0x7F) + 1),
          Offset: (uint)((word >> 16) & 0x7F),
          Nonce: 0,
          ChecksumType: (BcacheFsChecksumType)((word >> 24) & 0xF),
          CompressionType: (BcacheFsCompressionType)((word >> 28) & 0xF),
          Checksum: new BcacheFsChecksum(word >> 32, 0));
        error = string.Empty;
        return true;
      }
      case BcacheFsExtentEntryType.Crc64: {
        var header = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var csumLo = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        crc = new BcacheFsExtentCrc(
          CompressedSize: (uint)(((header >> 3) & 0x1FF) + 1),
          UncompressedSize: (uint)(((header >> 12) & 0x1FF) + 1),
          Offset: (uint)((header >> 21) & 0x1FF),
          Nonce: (uint)((header >> 30) & 0x3FF),
          ChecksumType: (BcacheFsChecksumType)((header >> 40) & 0xF),
          CompressionType: (BcacheFsCompressionType)((header >> 44) & 0xF),
          Checksum: new BcacheFsChecksum(csumLo, (header >> 48) & 0xFFFF));
        error = string.Empty;
        return true;
      }
      case BcacheFsExtentEntryType.Crc128: {
        var header = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        crc = new BcacheFsExtentCrc(
          CompressedSize: (uint)(((header >> 4) & 0x1FFF) + 1),
          UncompressedSize: (uint)(((header >> 17) & 0x1FFF) + 1),
          Offset: (uint)((header >> 30) & 0x1FFF),
          Nonce: (uint)((header >> 43) & 0x1FFF),
          ChecksumType: (BcacheFsChecksumType)((header >> 56) & 0xF),
          CompressionType: (BcacheFsCompressionType)((header >> 60) & 0xF),
          Checksum: BcacheFsChecksum.Read(bytes.AsSpan(8)));
        error = string.Empty;
        return true;
      }
      default:
        error = $"extent entry type {entry.RawType} is not a checksum/compression descriptor.";
        return false;
    }
  }

  private static ulong ReadNativeUInt64(ReadOnlySpan<byte> bytes, bool bigEndian)
    => bigEndian
      ? BinaryPrimitives.ReadUInt64BigEndian(bytes)
      : BinaryPrimitives.ReadUInt64LittleEndian(bytes);

  private static Bpos ReadStoredBpos(ReadOnlySpan<byte> bytes, bool bigEndian) {
    if (!bigEndian)
      return ReadBpos(bytes);

    Span<byte> canonical = stackalloc byte[20];
    bytes[..20].CopyTo(canonical);
    canonical.Reverse();
    return ReadBpos(canonical);
  }
}

internal enum BcacheFsExtentEntryType : byte {
  Pointer = 0,
  Crc32 = 1,
  Crc64 = 2,
  Crc128 = 3,
  StripePointer = 4,
  RebalanceV1 = 5,
  Flags = 6,
  Reconcile = 7,
  ReconcileBackpointer = 8,
}

internal sealed record BcacheFsExtentEntry(
  int RawType,
  BcacheFsExtentEntryType? KnownType,
  byte[] RawBytes);

internal readonly record struct BcacheFsExtentPointer(
  byte Device,
  long Sector,
  byte Generation,
  bool Cached,
  bool Unused,
  bool Unwritten,
  ulong RawWord);

internal sealed record BcacheFsBtreePointer(
  bool Legacy,
  ulong Sequence,
  ushort SectorsWritten,
  ushort Flags,
  Bpos MinKey,
  Bpos MaxKey,
  IReadOnlyList<BcacheFsExtentEntry> Entries,
  IReadOnlyList<BcacheFsExtentPointer> Replicas) {
  internal bool RangeUpdated => (this.Flags & 1) != 0;
}

internal sealed record BcacheFsExtentCrc(
  uint CompressedSize,
  uint UncompressedSize,
  uint Offset,
  uint Nonce,
  BcacheFsChecksumType ChecksumType,
  BcacheFsCompressionType CompressionType,
  BcacheFsChecksum Checksum) {
  internal bool Compressed => this.CompressionType is not
    (BcacheFsCompressionType.None or BcacheFsCompressionType.Incompressible);
  internal bool Encoded => this.ChecksumType != BcacheFsChecksumType.None || this.Compressed;
}
