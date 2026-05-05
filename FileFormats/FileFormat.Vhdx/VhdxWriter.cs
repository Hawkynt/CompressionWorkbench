using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Vhdx;

/// <summary>
/// Writes spec-compliant Microsoft VHDX (MS-VHDX v2) virtual hard-disk images
/// from a raw disk byte buffer. Produces a fixed-payload (non-differencing,
/// no-log) container — every block is marked PAYLOAD_BLOCK_FULLY_PRESENT.
/// </summary>
/// <remarks>
/// Layout produced (defaults: 16 MiB block size, 512 B logical, 4096 B physical sector):
/// <code>
///   0x000000  File Type Identifier  (64 KiB: "vhdxfile" + UTF-16LE creator)
///   0x010000  Header 1              (64 KiB; SequenceNumber=1, CRC-32C valid)
///   0x020000  Header 2              (64 KiB; SequenceNumber=2, CRC-32C valid; active)
///   0x030000  Region Table 1        (64 KiB; BAT + Metadata entries, CRC-32C valid)
///   0x040000  Region Table 2        (64 KiB; identical copy of Region Table 1)
///   0x050000  reserved              (zeroed; up to 1 MiB)
///   0x100000  Metadata Region       (64 KiB; 5 required items)
///   0x110000  BAT                   (≥64 KiB, 1 MiB-aligned, ⌈disk/16MiB⌉ entries)
///   ≥1 MiB    Block Data            (16 MiB blocks, 1 MiB-aligned)
/// </code>
/// All multi-byte integers in VHDX are little-endian per the spec.
/// CRC-32C (Castagnoli polynomial) per [MS-VHDX] 3.1.x — uses
/// <see cref="Crc32.Castagnoli"/> with the field zeroed during computation.
/// </remarks>
public sealed class VhdxWriter {

  // Region anchors (1 MiB header reservation, then 1 MiB-aligned data).
  private const long FileTypeIdentifierOffset = 0x00000;
  private const long Header1Offset = 0x10000;
  private const long Header2Offset = 0x20000;
  private const long RegionTable1Offset = 0x30000;
  private const long RegionTable2Offset = 0x40000;
  private const long MetadataRegionOffset = 0x100000;   // 1 MiB-aligned
  private const int RegionSize = 0x10000;               // 64 KiB
  private const int MetadataRegionSize = 0x10000;       // 64 KiB
  private const long OneMib = 0x100000;
  private const int FixedBlockSize = 16 * 1024 * 1024;  // 16 MiB
  private const ushort LogicalSectorSize = 512;
  private const uint PhysicalSectorSize = 4096;

  // Spec GUIDs (well-known per [MS-VHDX] 3.1.x).
  private static readonly Guid BatRegionGuid       = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
  private static readonly Guid MetadataRegionGuid  = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
  private static readonly Guid FileParametersGuid  = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
  private static readonly Guid VirtualDiskSizeGuid = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
  private static readonly Guid Page83DataGuid      = new("BECA12AB-B2E6-4523-93EF-C309E000C746");
  private static readonly Guid LogicalSectorGuid   = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
  private static readonly Guid PhysicalSectorGuid  = new("CDA348C7-445D-4471-9CC9-E9885251C556");

  // Metadata item IsRequired flag bit.
  private const uint MetadataFlagIsRequired = 1u << 2;

  private byte[]? _diskData;
  private string _creator = "CompressionWorkbench VHDX 1.0";

  /// <summary>Sets the raw disk data to embed. Will be padded to the next 16 MiB boundary.</summary>
  public void SetDiskData(byte[] data) => this._diskData = data;

  /// <summary>Sets the Creator string written into the File Type Identifier (max 256 UTF-16LE chars).</summary>
  public void SetCreator(string creator) => this._creator = creator;

  /// <summary>
  /// Builds the VHDX container as a single byte array.
  /// </summary>
  public byte[] Build() {
    var disk = this._diskData ?? [];

    // VHDX Virtual Disk Size must be a multiple of LogicalSectorSize, and
    // each backing block is 16 MiB. Round up disk length to a whole block.
    var blockCount = (long)((disk.LongLength + FixedBlockSize - 1) / FixedBlockSize);
    if (blockCount == 0) blockCount = 1;
    var virtualDiskSize = (ulong)blockCount * (ulong)FixedBlockSize;

    // BAT sizing: one 8-byte entry per block, padded up to 1 MiB alignment so the
    // payload region begins at a 1 MiB boundary. Region length itself must be a
    // multiple of 1 MiB per [MS-VHDX] 2.4 (Region Table Entry).
    var batByteLength = blockCount * 8;
    var batRegionLength = AlignUp(batByteLength, OneMib);
    var batRegionOffset = MetadataRegionOffset + AlignUp(MetadataRegionSize, OneMib);
    var payloadOffset = batRegionOffset + batRegionLength;
    var totalSize = payloadOffset + (long)virtualDiskSize;

    var result = new byte[totalSize];
    var span = result.AsSpan();

    WriteFileTypeIdentifier(span);
    WriteHeader(span.Slice((int)Header1Offset, RegionSize), sequenceNumber: 1);
    WriteHeader(span.Slice((int)Header2Offset, RegionSize), sequenceNumber: 2);

    WriteRegionTable(
      span.Slice((int)RegionTable1Offset, RegionSize),
      batOffset: batRegionOffset,
      batLength: (uint)batRegionLength,
      metadataOffset: MetadataRegionOffset,
      metadataLength: MetadataRegionSize);
    // Region Table 2 is a byte-for-byte copy of Region Table 1.
    span.Slice((int)RegionTable1Offset, RegionSize)
        .CopyTo(span.Slice((int)RegionTable2Offset, RegionSize));

    WriteMetadataRegion(span.Slice((int)MetadataRegionOffset, MetadataRegionSize), virtualDiskSize);
    WriteBat(span.Slice((int)batRegionOffset, (int)batRegionLength), payloadOffset, blockCount);

    // Disk data: copy provided bytes; remainder of the last block is left zero.
    if (disk.Length > 0)
      disk.AsSpan().CopyTo(span[(int)payloadOffset..]);

    return result;
  }

  // ─────────────────────────────────────────────────────────────────────
  // File Type Identifier
  // ─────────────────────────────────────────────────────────────────────

  private void WriteFileTypeIdentifier(Span<byte> file) {
    var region = file.Slice((int)FileTypeIdentifierOffset, RegionSize);
    "vhdxfile"u8.CopyTo(region);

    // Creator: up to 256 UTF-16LE code units (512 bytes).
    var creator = this._creator.Length > 256 ? this._creator[..256] : this._creator;
    var bytes = Encoding.Unicode.GetBytes(creator);
    var max = Math.Min(bytes.Length, 512);
    bytes.AsSpan(0, max).CopyTo(region.Slice(8, max));
  }

  // ─────────────────────────────────────────────────────────────────────
  // Header (64 KiB)
  // ─────────────────────────────────────────────────────────────────────

  private static void WriteHeader(Span<byte> region, ulong sequenceNumber) {
    // "head" signature (little-endian).
    region[0] = (byte)'h';
    region[1] = (byte)'e';
    region[2] = (byte)'a';
    region[3] = (byte)'d';
    // Checksum (offset 4) left zero during computation.
    BinaryPrimitives.WriteUInt64LittleEndian(region[8..], sequenceNumber);

    // FileWriteGuid (16), DataWriteGuid (32), LogGuid (48). LogGuid stays
    // zero — no log means no replay required when the consumer opens us.
    WriteGuidLe(region.Slice(16, 16), Guid.NewGuid());
    WriteGuidLe(region.Slice(32, 16), Guid.NewGuid());
    // LogGuid: zeroed.

    BinaryPrimitives.WriteUInt16LittleEndian(region[64..], 0);            // LogVersion = 0
    BinaryPrimitives.WriteUInt16LittleEndian(region[66..], 1);            // Version = 1
    BinaryPrimitives.WriteUInt32LittleEndian(region[68..], 0);            // LogLength = 0
    BinaryPrimitives.WriteUInt64LittleEndian(region[72..], 0);            // LogOffset = 0
    // Reserved[4016] already zeroed.

    var crc = Crc32.Compute(region, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(region[4..], crc);
  }

  // ─────────────────────────────────────────────────────────────────────
  // Region Table (64 KiB) — 16-byte header + 32-byte entries.
  // ─────────────────────────────────────────────────────────────────────

  private static void WriteRegionTable(Span<byte> region, long batOffset, uint batLength,
                                       long metadataOffset, uint metadataLength) {
    // "regi" signature.
    region[0] = (byte)'r';
    region[1] = (byte)'e';
    region[2] = (byte)'g';
    region[3] = (byte)'i';
    // Checksum (4) left zero during computation.
    BinaryPrimitives.WriteUInt32LittleEndian(region[8..], 2);             // EntryCount
    BinaryPrimitives.WriteUInt32LittleEndian(region[12..], 0);            // Reserved

    // Entry 0: BAT
    var bat = region.Slice(16, 32);
    WriteGuidLe(bat[..16], BatRegionGuid);
    BinaryPrimitives.WriteUInt64LittleEndian(bat[16..], (ulong)batOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(bat[24..], batLength);
    BinaryPrimitives.WriteUInt32LittleEndian(bat[28..], 1);               // Required = true

    // Entry 1: Metadata
    var meta = region.Slice(48, 32);
    WriteGuidLe(meta[..16], MetadataRegionGuid);
    BinaryPrimitives.WriteUInt64LittleEndian(meta[16..], (ulong)metadataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(meta[24..], metadataLength);
    BinaryPrimitives.WriteUInt32LittleEndian(meta[28..], 1);              // Required = true

    var crc = Crc32.Compute(region, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(region[4..], crc);
  }

  // ─────────────────────────────────────────────────────────────────────
  // Metadata Region (64 KiB) — header + 5 required items.
  // [MS-VHDX] 2.6: header at offset 0, table entries at offset 32, payload
  // anywhere after (we place items at fixed offsets at the tail of the region).
  // ─────────────────────────────────────────────────────────────────────

  private static void WriteMetadataRegion(Span<byte> region, ulong virtualDiskSize) {
    // Metadata header.
    "metadata"u8.CopyTo(region);                                          // Signature
    BinaryPrimitives.WriteUInt16LittleEndian(region[8..], 0);             // Reserved
    BinaryPrimitives.WriteUInt16LittleEndian(region[10..], 5);            // EntryCount = 5
    // Reserved2[20] zero (offset 12..31).

    // Item payloads packed at the end of the region for simplicity.
    // Sizes: FileParameters=8, VirtualDiskSize=8, Page83=16, LogicalSector=4, PhysicalSector=4.
    // Total = 40 bytes; place at MetadataRegionSize - 64 (16-byte alignment).
    var payloadStart = MetadataRegionSize - 64;

    // Layout of each metadata item (5 entries, 32 bytes each, starting at offset 32):
    //   ItemId (16)  Offset (4)  Length (4)  Flags (4)  Reserved2 (4)
    var entryBase = 32;

    // Item 1: File Parameters (8 bytes: BlockSize uint32, flags uint32 with LeaveBlockAllocated/HasParent bits).
    var fpOff = payloadStart;
    WriteMetaEntry(region, entryBase + 0 * 32, FileParametersGuid, fpOff, 8, isRequired: true, isVirtualDisk: false);
    BinaryPrimitives.WriteUInt32LittleEndian(region[fpOff..], (uint)FixedBlockSize);
    // Flags uint32: bit 0 LeaveBlocksAllocated, bit 1 HasParent. Both 0 → fixed, no parent.
    BinaryPrimitives.WriteUInt32LittleEndian(region[(fpOff + 4)..], 0u);

    // Item 2: Virtual Disk Size (8 bytes: uint64 disk size in bytes).
    var vdsOff = payloadStart + 8;
    WriteMetaEntry(region, entryBase + 1 * 32, VirtualDiskSizeGuid, vdsOff, 8, isRequired: true, isVirtualDisk: true);
    BinaryPrimitives.WriteUInt64LittleEndian(region[vdsOff..], virtualDiskSize);

    // Item 3: Page 83 Data (16-byte GUID).
    var p83Off = payloadStart + 16;
    WriteMetaEntry(region, entryBase + 2 * 32, Page83DataGuid, p83Off, 16, isRequired: true, isVirtualDisk: true);
    WriteGuidLe(region.Slice(p83Off, 16), Guid.NewGuid());

    // Item 4: Logical Sector Size (4 bytes: uint32).
    var lsOff = payloadStart + 32;
    WriteMetaEntry(region, entryBase + 3 * 32, LogicalSectorGuid, lsOff, 4, isRequired: true, isVirtualDisk: true);
    BinaryPrimitives.WriteUInt32LittleEndian(region[lsOff..], LogicalSectorSize);

    // Item 5: Physical Sector Size (4 bytes: uint32).
    var psOff = payloadStart + 36;
    WriteMetaEntry(region, entryBase + 4 * 32, PhysicalSectorGuid, psOff, 4, isRequired: true, isVirtualDisk: true);
    BinaryPrimitives.WriteUInt32LittleEndian(region[psOff..], PhysicalSectorSize);
  }

  private static void WriteMetaEntry(Span<byte> region, int entryOffset, Guid itemId,
                                     int payloadOffsetInRegion, uint length,
                                     bool isRequired, bool isVirtualDisk) {
    var entry = region.Slice(entryOffset, 32);
    WriteGuidLe(entry[..16], itemId);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)payloadOffsetInRegion);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], length);
    // Flags: bit 0 IsUser, bit 1 IsVirtualDisk, bit 2 IsRequired.
    var flags = 0u;
    if (isVirtualDisk) flags |= 1u << 1;
    if (isRequired)    flags |= MetadataFlagIsRequired;
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], flags);
    // Reserved2 (28..31) zero.
  }

  // ─────────────────────────────────────────────────────────────────────
  // BAT (Block Allocation Table)
  // 64-bit entries, low 3 bits = state, bits 20..63 = file offset (1 MiB units).
  // PAYLOAD_BLOCK_FULLY_PRESENT = 6
  // ─────────────────────────────────────────────────────────────────────

  private static void WriteBat(Span<byte> bat, long firstBlockFileOffset, long blockCount) {
    const ulong stateFullyPresent = 6;
    for (var i = 0L; i < blockCount; i++) {
      var blockFileOffset = (ulong)(firstBlockFileOffset + i * FixedBlockSize);
      // Bits 0..19 reserved/state; bits 20..63 = offset / 1 MiB.
      var fileOffsetMib = blockFileOffset / (ulong)OneMib;
      var entry = (fileOffsetMib << 20) | stateFullyPresent;
      BinaryPrimitives.WriteUInt64LittleEndian(bat[(int)(i * 8)..], entry);
    }
  }

  // ─────────────────────────────────────────────────────────────────────
  // Helpers
  // ─────────────────────────────────────────────────────────────────────

  private static long AlignUp(long value, long boundary)
    => ((value + boundary - 1) / boundary) * boundary;

  private static void WriteGuidLe(Span<byte> dst, Guid guid) {
    // Guid.ToByteArray() emits the standard mixed-endian layout used by both
    // Microsoft GUIDs and [MS-VHDX]: Data1/Data2/Data3 little-endian, Data4 big-endian.
    guid.ToByteArray().CopyTo(dst);
  }
}
