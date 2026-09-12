using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Vhdx;

/// <summary>
/// Writes standalone dynamic VHDX images using the MS-VHDX layout and BAT
/// semantics. Zero payload blocks are represented by PAYLOAD_BLOCK_ZERO and
/// consume no payload space; non-zero blocks are FULLY_PRESENT.
/// </summary>
public sealed class VhdxWriter {
  private const long Header1Offset = 0x10000;
  private const long Header2Offset = 0x20000;
  private const long RegionTable1Offset = 0x30000;
  private const long RegionTable2Offset = 0x40000;
  private const long LogOffset = 0x100000;
  private const long LogLength = 0x100000;
  private const long MetadataRegionOffset = 0x200000;
  private const int RegionSize = 0x10000;
  private const int MetadataRegionSize = 0x10000;
  private const int HeaderStructureSize = 4096;
  private const long OneMib = 0x100000;
  private const int BlockSize = 16 * 1024 * 1024;
  private const uint LogicalSectorSize = 512;
  private const uint PhysicalSectorSize = 4096;

  private const ulong PayloadBlockZero = 2;
  private const ulong PayloadBlockFullyPresent = 6;

  private static readonly Guid BatRegionGuid = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
  private static readonly Guid MetadataRegionGuid = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
  private static readonly Guid FileParametersGuid = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
  private static readonly Guid VirtualDiskSizeGuid = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
  private static readonly Guid Page83DataGuid = new("BECA12AB-B2E6-4523-93EF-C309E000C746");
  private static readonly Guid LogicalSectorGuid = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
  private static readonly Guid PhysicalSectorGuid = new("CDA348C7-445D-4471-9CC9-E9885251C556");

  private byte[]? _diskData;
  private string _creator = "CompressionWorkbench VHDX 1.0";

  /// <summary>Sets the raw virtual-disk contents.</summary>
  public void SetDiskData(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._diskData = data;
  }

  /// <summary>Sets the creator string in the file-type identifier region.</summary>
  public void SetCreator(string creator) => this._creator = creator ?? string.Empty;

  /// <summary>Builds a standalone dynamic VHDX image.</summary>
  public byte[] Build() {
    var disk = this._diskData ?? [];
    var virtualSize = Math.Max((long)LogicalSectorSize, AlignUp(disk.LongLength, LogicalSectorSize));
    var payloadBlockCount = checked((int)((virtualSize + BlockSize - 1) / BlockSize));
    var chunkRatio = checked((int)(((1L << 23) * LogicalSectorSize) / BlockSize));
    var batEntryCount = payloadBlockCount + (payloadBlockCount - 1) / chunkRatio;
    var batRegionLength = AlignUp((long)batEntryCount * 8, OneMib);
    var batRegionOffset = MetadataRegionOffset + OneMib;
    var payloadOffset = batRegionOffset + batRegionLength;

    var allocated = new bool[payloadBlockCount];
    var allocatedCount = 0;
    for (var i = 0; i < payloadBlockCount; ++i) {
      var offset = (long)i * BlockSize;
      var length = (int)Math.Min(BlockSize, Math.Max(0, disk.LongLength - offset));
      if (length <= 0 || IsAllZero(disk.AsSpan((int)offset, length))) continue;
      allocated[i] = true;
      ++allocatedCount;
    }

    var totalSize = checked(payloadOffset + (long)allocatedCount * BlockSize);
    if (totalSize > int.MaxValue)
      throw new NotSupportedException("VHDX writer currently requires a container smaller than 2 GiB.");

    var result = new byte[(int)totalSize];
    var span = result.AsSpan();

    WriteFileTypeIdentifier(span);
    WriteHeader(span.Slice((int)Header1Offset, RegionSize), sequenceNumber: 1);
    WriteHeader(span.Slice((int)Header2Offset, RegionSize), sequenceNumber: 2);
    WriteRegionTable(span.Slice((int)RegionTable1Offset, RegionSize), batRegionOffset,
      checked((uint)batRegionLength), MetadataRegionOffset, checked((uint)OneMib));
    span.Slice((int)RegionTable1Offset, RegionSize)
      .CopyTo(span.Slice((int)RegionTable2Offset, RegionSize));
    WriteMetadataRegion(span.Slice((int)MetadataRegionOffset, (int)OneMib), checked((ulong)virtualSize));

    WriteBatAndPayload(span, disk, allocated, payloadBlockCount, chunkRatio, batRegionOffset, payloadOffset);
    return result;
  }

  private void WriteFileTypeIdentifier(Span<byte> file) {
    var region = file[..RegionSize];
    "vhdxfile"u8.CopyTo(region);
    var creator = this._creator.Length > 256 ? this._creator[..256] : this._creator;
    var bytes = Encoding.Unicode.GetBytes(creator);
    bytes.AsSpan(0, Math.Min(bytes.Length, 512)).CopyTo(region[8..]);
  }

  private static void WriteHeader(Span<byte> region, ulong sequenceNumber) {
    "head"u8.CopyTo(region);
    BinaryPrimitives.WriteUInt64LittleEndian(region[8..], sequenceNumber);
    WriteGuidLe(region[16..32], Guid.NewGuid());
    WriteGuidLe(region[32..48], Guid.NewGuid());
    BinaryPrimitives.WriteUInt16LittleEndian(region[64..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(region[66..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(region[68..], (uint)LogLength);
    BinaryPrimitives.WriteUInt64LittleEndian(region[72..], (ulong)LogOffset);
    var crc = Crc32.Compute(region[..HeaderStructureSize], Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(region[4..], crc);
  }

  private static void WriteRegionTable(
      Span<byte> region,
      long batOffset,
      uint batLength,
      long metadataOffset,
      uint metadataLength) {
    "regi"u8.CopyTo(region);
    BinaryPrimitives.WriteUInt32LittleEndian(region[8..], 2);
    WriteRegionEntry(region[16..48], BatRegionGuid, batOffset, batLength);
    WriteRegionEntry(region[48..80], MetadataRegionGuid, metadataOffset, metadataLength);
    var crc = Crc32.Compute(region, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(region[4..], crc);
  }

  private static void WriteRegionEntry(Span<byte> entry, Guid guid, long offset, uint length) {
    WriteGuidLe(entry[..16], guid);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], checked((ulong)offset));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], length);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], 1);
  }

  private static void WriteMetadataRegion(Span<byte> region, ulong virtualDiskSize) {
    "metadata"u8.CopyTo(region);
    BinaryPrimitives.WriteUInt16LittleEndian(region[10..], 5);
    const int payloadStart = 0x10000;
    const int tableStart = 32;

    WriteMetadataEntry(region, tableStart + 0 * 32, FileParametersGuid, payloadStart, 8, isVirtualDisk: false);
    BinaryPrimitives.WriteUInt32LittleEndian(region[payloadStart..], (uint)BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(region[(payloadStart + 4)..], 0); // dynamic, no parent

    WriteMetadataEntry(region, tableStart + 1 * 32, VirtualDiskSizeGuid, payloadStart + 8, 8, isVirtualDisk: true);
    BinaryPrimitives.WriteUInt64LittleEndian(region[(payloadStart + 8)..], virtualDiskSize);

    WriteMetadataEntry(region, tableStart + 2 * 32, Page83DataGuid, payloadStart + 16, 16, isVirtualDisk: true);
    WriteGuidLe(region[(payloadStart + 16)..(payloadStart + 32)], Guid.NewGuid());

    WriteMetadataEntry(region, tableStart + 3 * 32, LogicalSectorGuid, payloadStart + 32, 4, isVirtualDisk: true);
    BinaryPrimitives.WriteUInt32LittleEndian(region[(payloadStart + 32)..], LogicalSectorSize);

    WriteMetadataEntry(region, tableStart + 4 * 32, PhysicalSectorGuid, payloadStart + 36, 4, isVirtualDisk: true);
    BinaryPrimitives.WriteUInt32LittleEndian(region[(payloadStart + 36)..], PhysicalSectorSize);
  }

  private static void WriteMetadataEntry(
      Span<byte> region,
      int entryOffset,
      Guid itemId,
      int payloadOffset,
      uint length,
      bool isVirtualDisk) {
    var entry = region.Slice(entryOffset, 32);
    WriteGuidLe(entry[..16], itemId);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], checked((uint)payloadOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], length);
    var flags = 1u << 2; // IsRequired
    if (isVirtualDisk) flags |= 1u << 1;
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], flags);
  }

  private static void WriteBatAndPayload(
      Span<byte> file,
      byte[] disk,
      bool[] allocated,
      int payloadBlockCount,
      int chunkRatio,
      long batRegionOffset,
      long payloadOffset) {
    var nextPayloadOffset = payloadOffset;
    for (var blockIndex = 0; blockIndex < payloadBlockCount; ++blockIndex) {
      var batIndex = PayloadBatIndex(blockIndex, chunkRatio);
      ulong entry;
      if (!allocated[blockIndex]) {
        entry = PayloadBlockZero;
      } else {
        entry = checked((ulong)(nextPayloadOffset / OneMib) << 20) | PayloadBlockFullyPresent;
        var sourceOffset = (long)blockIndex * BlockSize;
        var sourceLength = (int)Math.Min(BlockSize, Math.Max(0, disk.LongLength - sourceOffset));
        if (sourceLength > 0)
          disk.AsSpan((int)sourceOffset, sourceLength).CopyTo(file.Slice((int)nextPayloadOffset, sourceLength));
        nextPayloadOffset += BlockSize;
      }

      BinaryPrimitives.WriteUInt64LittleEndian(
        file.Slice(checked((int)(batRegionOffset + batIndex * 8L)), 8), entry);
    }
  }

  internal static int PayloadBatIndex(int payloadBlockIndex, int chunkRatio)
    => payloadBlockIndex + payloadBlockIndex / chunkRatio;

  private static bool IsAllZero(ReadOnlySpan<byte> data) {
    foreach (var value in data)
      if (value != 0) return false;
    return true;
  }

  private static long AlignUp(long value, long boundary)
    => checked((value + boundary - 1) / boundary * boundary);

  private static void WriteGuidLe(Span<byte> target, Guid guid)
    => guid.ToByteArray().CopyTo(target);
}
