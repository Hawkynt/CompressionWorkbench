using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses MBR (Master Boot Record) partition tables, including extended/logical partition chains.
/// The MBR sits at LBA 0 (offset 0) of a disk image, with 4 primary partition entries at offset 0x1BE.
/// </summary>
public static class MbrParser {

  /// <summary>MBR boot signature at offset 510.</summary>
  private const ushort BootSignature = 0xAA55;

  /// <summary>Offset of the first partition entry in the MBR.</summary>
  private const int PartitionTableOffset = 0x1BE;

  /// <summary>Size of each MBR partition entry.</summary>
  private const int EntrySize = 16;

  /// <summary>Standard sector size.</summary>
  private const int SectorSize = 512;

  /// <summary>
  /// Checks whether the given data starts with a valid MBR.
  /// </summary>
  /// <param name="data">At least 512 bytes of disk image data starting at LBA 0.</param>
  /// <returns><c>true</c> if the data has a valid MBR signature.</returns>
  public static bool IsMbr(ReadOnlySpan<byte> data)
    => data.Length >= SectorSize
       && BinaryPrimitives.ReadUInt16LittleEndian(data[510..]) == BootSignature;

  /// <summary>
  /// Parses all partitions from an MBR, including extended/logical partitions.
  /// </summary>
  /// <param name="diskData">The full disk image as a readable stream.</param>
  /// <returns>A list of partition entries.</returns>
  public static List<PartitionEntry> Parse(Stream diskData) {
    var mbr = new byte[SectorSize];
    diskData.Position = 0;
    diskData.ReadExactly(mbr);

    if (BinaryPrimitives.ReadUInt16LittleEndian(mbr.AsSpan(510)) != BootSignature)
      throw new InvalidDataException("Invalid MBR: missing boot signature 0xAA55.");

    var result = new List<PartitionEntry>();
    var index = 0;

    for (var i = 0; i < 4; i++) {
      var entry = ParseEntry(mbr.AsSpan(PartitionTableOffset + i * EntrySize));
      if (entry.Type == 0x00)
        continue;

      if (entry.Type is 0x05 or 0x0F or 0x85) {
        // Extended partition: walk the chain.
        var extStart = entry.LbaStart;
        ParseExtendedChain(diskData, extStart, extStart, result, ref index);
      } else {
        result.Add(new() {
          Index = index++,
          StartOffset = (long)entry.LbaStart * SectorSize,
          Size = (long)entry.SectorCount * SectorSize,
          TypeName = PartitionTypeDatabase.GetMbrTypeName(entry.Type),
          IsActive = entry.IsActive,
          TypeCode = $"0x{entry.Type:X2}",
          Source = "MBR"
        });
      }
    }

    return result;
  }

  /// <summary>
  /// Parses the 4 primary partition entries from an MBR without following extended chains.
  /// Operates on a 512-byte buffer (no stream needed).
  /// </summary>
  /// <param name="mbr">The first 512 bytes of the disk image.</param>
  /// <returns>A list of primary partition entries (excluding empty entries).</returns>
  public static List<PartitionEntry> ParsePrimary(ReadOnlySpan<byte> mbr) {
    if (mbr.Length < SectorSize)
      throw new ArgumentException("MBR data must be at least 512 bytes.", nameof(mbr));

    var result = new List<PartitionEntry>();
    var index = 0;

    for (var i = 0; i < 4; i++) {
      var entry = ParseEntry(mbr.Slice(PartitionTableOffset + i * EntrySize, EntrySize));
      if (entry.Type == 0x00)
        continue;

      result.Add(new() {
        Index = index++,
        StartOffset = (long)entry.LbaStart * SectorSize,
        Size = (long)entry.SectorCount * SectorSize,
        TypeName = PartitionTypeDatabase.GetMbrTypeName(entry.Type),
        IsActive = entry.IsActive,
        TypeCode = $"0x{entry.Type:X2}",
        Source = entry.Type is 0x05 or 0x0F or 0x85 ? "MBR (Extended Container)" : "MBR"
      });
    }

    return result;
  }

  private static void ParseExtendedChain(Stream diskData, uint ebrLba, uint extStart, List<PartitionEntry> result, ref int index) {
    var seen = new HashSet<uint>();

    while (ebrLba != 0 && seen.Add(ebrLba)) {
      var ebr = new byte[SectorSize];
      diskData.Position = (long)ebrLba * SectorSize;
      if (diskData.Read(ebr) < SectorSize)
        break;

      // Entry 0: the logical partition (relative to this EBR's LBA).
      var logical = ParseEntry(ebr.AsSpan(PartitionTableOffset));
      if (logical.Type != 0x00 && logical.SectorCount > 0) {
        var logicalLba = ebrLba + logical.LbaStart;
        result.Add(new() {
          Index = index++,
          StartOffset = (long)logicalLba * SectorSize,
          Size = (long)logical.SectorCount * SectorSize,
          TypeName = PartitionTypeDatabase.GetMbrTypeName(logical.Type),
          IsActive = logical.IsActive,
          TypeCode = $"0x{logical.Type:X2}",
          Source = "EBR"
        });
      }

      // Entry 1: pointer to the next EBR in the chain (relative to the extended partition start).
      var next = ParseEntry(ebr.AsSpan(PartitionTableOffset + EntrySize));
      if (next.Type == 0x00 || next.SectorCount == 0)
        break;

      ebrLba = extStart + next.LbaStart;
    }
  }

  private static RawEntry ParseEntry(ReadOnlySpan<byte> data) => new() {
    IsActive = (data[0] & 0x80) != 0,
    Type = data[4],
    LbaStart = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
    SectorCount = BinaryPrimitives.ReadUInt32LittleEndian(data[12..])
  };

  private readonly struct RawEntry {
    public bool IsActive { get; init; }
    public byte Type { get; init; }
    public uint LbaStart { get; init; }
    public uint SectorCount { get; init; }
  }
}
