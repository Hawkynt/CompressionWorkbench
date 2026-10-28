using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses MBR (Master Boot Record) partition tables, including extended/logical partition chains.
/// The MBR sits at LBA 0 (offset 0) of a disk image, with 4 primary partition entries at offset 0x1BE.
/// The entries count in logical blocks: 512 bytes on a classic or 512e disk, 4096 on a 4Kn disk.
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

  /// <summary>Cap on logical partitions walked in one chain, so a damaged or hostile chain cannot spin.</summary>
  private const int MaxLogicalPartitions = 256;

  /// <summary>
  /// Checks whether the given data starts with a valid MBR.
  /// </summary>
  /// <param name="data">At least 512 bytes of disk image data starting at LBA 0.</param>
  /// <returns><c>true</c> if the data has a valid MBR signature.</returns>
  public static bool IsMbr(ReadOnlySpan<byte> data)
    => data.Length >= SectorSize
       && BinaryPrimitives.ReadUInt16LittleEndian(data[510..]) == BootSignature;

  /// <summary>
  /// Checks whether sector 0 holds a partition table that is actually usable, rather than
  /// merely ending in the 0x55AA boot signature — which every FAT, NTFS and exFAT volume boot
  /// record does as well.
  /// </summary>
  /// <remarks>
  /// Rules, after the UEFI specification's description of the legacy MBR (UEFI 2.10, §5.2.1):
  /// every one of the four entries carries a boot indicator of 0x00 or 0x80; an entry with
  /// partition type 0x00 is unused; a used entry starts at LBA 1 or later, spans at least one
  /// sector and starts inside the image; used entries do not overlap; and at least one entry is
  /// used. An entry may run past the end of the image (a truncated dump, or the 0xFFFFFFFF
  /// length of a protective MBR) as long as it starts inside it.
  /// </remarks>
  /// <param name="sector0">At least the first 512 bytes of the image.</param>
  /// <param name="diskLength">Length of the whole image in bytes.</param>
  /// <param name="sectorSize">Logical block size the entries' LBAs are counted in.</param>
  public static bool HasValidPartitionTable(ReadOnlySpan<byte> sector0, long diskLength, int sectorSize = SectorSize) {
    if (!IsMbr(sector0)) return false;

    Span<(ulong Start, ulong End)> used = stackalloc (ulong, ulong)[4];
    var count = 0;
    for (var i = 0; i < 4; ++i) {
      var raw = sector0.Slice(PartitionTableOffset + i * EntrySize, EntrySize);
      if (raw[0] is not (0x00 or 0x80)) return false;

      var entry = ParseEntry(raw);
      if (entry.Type == 0x00) continue;
      if (entry.LbaStart == 0 || entry.SectorCount == 0) return false;

      var start = (ulong)entry.LbaStart * (ulong)sectorSize;
      if (start >= (ulong)diskLength) return false;

      var end = start + (ulong)entry.SectorCount * (ulong)sectorSize;
      for (var j = 0; j < count; ++j)
        if (start < used[j].End && used[j].Start < end)
          return false;
      used[count++] = (start, end);
    }
    return count > 0;
  }

  /// <summary>
  /// Parses all partitions from an MBR, including extended/logical partitions.
  /// </summary>
  /// <param name="diskData">The full disk image as a readable stream.</param>
  /// <param name="sectorSize">Logical block size the entries' LBAs are counted in (512, or 4096 on a 4Kn disk).</param>
  /// <returns>A list of partition entries.</returns>
  public static List<PartitionEntry> Parse(Stream diskData, int sectorSize = SectorSize) {
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

      if (IsExtended(entry.Type)) {
        ParseExtendedChain(diskData, entry.LbaStart, entry.LbaStart, entry.SectorCount, sectorSize, result, ref index);
      } else {
        result.Add(new() {
          Index = index++,
          StartOffset = (long)entry.LbaStart * sectorSize,
          Size = (long)entry.SectorCount * sectorSize,
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
  /// <param name="sectorSize">Logical block size the entries' LBAs are counted in.</param>
  /// <returns>A list of primary partition entries (excluding empty entries).</returns>
  public static List<PartitionEntry> ParsePrimary(ReadOnlySpan<byte> mbr, int sectorSize = SectorSize) {
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
        StartOffset = (long)entry.LbaStart * sectorSize,
        Size = (long)entry.SectorCount * sectorSize,
        TypeName = PartitionTypeDatabase.GetMbrTypeName(entry.Type),
        IsActive = entry.IsActive,
        TypeCode = $"0x{entry.Type:X2}",
        Source = IsExtended(entry.Type) ? "MBR (Extended Container)" : "MBR"
      });
    }

    return result;
  }

  private static bool IsExtended(byte type) => type is 0x05 or 0x0F or 0x85;

  /// <summary>
  /// Walks the chain of extended boot records. Each EBR's first entry is a logical partition
  /// whose start is relative to that EBR; its second entry links to the next EBR, relative to
  /// the start of the extended partition. The walk stops at a loop, at an EBR without the boot
  /// signature, at a link that leaves the extended partition, and after
  /// <see cref="MaxLogicalPartitions"/> records.
  /// </summary>
  private static void ParseExtendedChain(Stream diskData, uint ebrLba, uint extStart, uint extSectors, int sectorSize,
      List<PartitionEntry> result, ref int index) {
    var seen = new HashSet<uint>();
    var extEnd = (ulong)extStart + extSectors;

    while (ebrLba != 0 && seen.Count < MaxLogicalPartitions && seen.Add(ebrLba)) {
      if (ebrLba < extStart || ebrLba >= extEnd)
        break;
      var ebr = new byte[SectorSize];
      diskData.Position = (long)ebrLba * sectorSize;
      if (diskData.ReadAtLeast(ebr, SectorSize, throwOnEndOfStream: false) < SectorSize)
        break;
      if (BinaryPrimitives.ReadUInt16LittleEndian(ebr.AsSpan(510)) != BootSignature)
        break;

      // Entry 0: the logical partition (relative to this EBR's LBA).
      var logical = ParseEntry(ebr.AsSpan(PartitionTableOffset));
      if (logical.Type != 0x00 && logical.SectorCount > 0) {
        var logicalLba = (long)ebrLba + logical.LbaStart;
        result.Add(new() {
          Index = index++,
          StartOffset = logicalLba * sectorSize,
          Size = (long)logical.SectorCount * sectorSize,
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

      var nextLba = (ulong)extStart + next.LbaStart;
      if (nextLba > uint.MaxValue) break;
      ebrLba = (uint)nextLba;
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
