#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.BcacheFs;

/// <summary>Decodes members_v1/v2 superblock fields without assuming one device.</summary>
internal static class BcacheFsMembers {
  internal const int CurrentMemberBytes = 296;
  internal const int LegacyMemberBytes = 56;

  internal static IReadOnlyList<BcacheFsMemberRecord> Read(BcacheFsSuperblockRecord superblock) {
    ArgumentNullException.ThrowIfNull(superblock);

    var v2 = superblock.FieldsOf(BcacheFsSuperblockFieldType.MembersV2).LastOrDefault();
    if (v2 != null)
      return ReadV2(v2);

    var v1 = superblock.FieldsOf(BcacheFsSuperblockFieldType.MembersV1).LastOrDefault();
    return v1 == null ? [] : ReadV1(v1);
  }

  private static IReadOnlyList<BcacheFsMemberRecord> ReadV2(BcacheFsSuperblockField field) {
    var bytes = field.RawBytes;
    if (bytes.Length < 16) return [];
    var memberBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
    if (memberBytes < 32) return [];

    var result = new List<BcacheFsMemberRecord>();
    for (var offset = 16; offset + memberBytes <= bytes.Length; offset += memberBytes)
      result.Add(Parse(bytes.AsSpan(offset, memberBytes), memberBytes));
    return result;
  }

  private static IReadOnlyList<BcacheFsMemberRecord> ReadV1(BcacheFsSuperblockField field) {
    var bytes = field.RawBytes;
    var result = new List<BcacheFsMemberRecord>();
    for (var offset = 8; offset + LegacyMemberBytes <= bytes.Length; offset += LegacyMemberBytes)
      result.Add(Parse(bytes.AsSpan(offset, LegacyMemberBytes), LegacyMemberBytes));
    return result;
  }

  private static BcacheFsMemberRecord Parse(ReadOnlySpan<byte> bytes, int memberBytes) {
    var flags = Read64(bytes, 40);
    return new BcacheFsMemberRecord {
      RawBytes = bytes.ToArray(),
      MemberBytes = memberBytes,
      UuidBytes = bytes[..Math.Min(16, bytes.Length)].ToArray(),
      BucketCount = Read64(bytes, 16),
      FirstBucket = Read16(bytes, 24),
      BucketSizeSectors = Read16(bytes, 26),
      BtreeBitmapShift = bytes.Length > 28 ? bytes[28] : (byte)0,
      LastMount = Read64(bytes, 32),
      Flags = flags,
      Sequence = Read64(bytes, 120),
      BtreeAllocatedBitmap = Read64(bytes, 128),
      LastJournalBucket = Read32(bytes, 136),
      LastJournalBucketOffset = Read32(bytes, 140),
    };
  }

  private static ulong Read64(ReadOnlySpan<byte> bytes, int offset)
    => offset + sizeof(ulong) <= bytes.Length
      ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)))
      : 0;

  private static uint Read32(ReadOnlySpan<byte> bytes, int offset)
    => offset + sizeof(uint) <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)))
      : 0;

  private static ushort Read16(ReadOnlySpan<byte> bytes, int offset)
    => offset + sizeof(ushort) <= bytes.Length
      ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, sizeof(ushort)))
      : (ushort)0;
}

internal sealed record BcacheFsMemberRecord {
  internal required byte[] RawBytes { get; init; }
  internal required int MemberBytes { get; init; }
  internal required byte[] UuidBytes { get; init; }
  internal required ulong BucketCount { get; init; }
  internal required ushort FirstBucket { get; init; }
  internal required ushort BucketSizeSectors { get; init; }
  internal required byte BtreeBitmapShift { get; init; }
  internal required ulong LastMount { get; init; }
  internal required ulong Flags { get; init; }
  internal required ulong Sequence { get; init; }
  internal required ulong BtreeAllocatedBitmap { get; init; }
  internal required uint LastJournalBucket { get; init; }
  internal required uint LastJournalBucketOffset { get; init; }

  internal BcacheFsMemberState State => (BcacheFsMemberState)(this.Flags & 0xF);
  internal bool Discard => (this.Flags & (1UL << 14)) != 0;
  internal byte DataAllowed => (byte)((this.Flags >> 15) & 0x1F);
  internal byte Group => (byte)((this.Flags >> 20) & 0xFF);
  internal byte Durability => (byte)((this.Flags >> 28) & 0x3);
  internal bool FreespaceInitialized => (this.Flags & (1UL << 30)) != 0;
  internal bool ResizeOnMount => (this.Flags & (1UL << 31)) != 0;
  internal bool Rotational => (this.Flags & (1UL << 32)) != 0;
  internal bool RotationalSet => (this.Flags & (1UL << 33)) != 0;
}

internal enum BcacheFsMemberState : byte {
  ReadWrite = 0,
  ReadOnly = 1,
  Evacuating = 2,
  Spare = 3,
}
