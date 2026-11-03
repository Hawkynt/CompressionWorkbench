using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses the Atari ST/TT hard-disk partition table (AHDI "root sector"), big-endian.
/// </summary>
/// <remarks>
/// <para>
/// Sector 0 holds the disk size in sectors at byte 0x1C2 and four 12-byte partition records at
/// 0x1C6: a flag byte (bit 0 = in use, bit 7 = boot), a three-character id (<c>GEM</c>,
/// <c>BGM</c>, <c>LNX</c>, <c>RAW</c>, <c>SWP</c>, <c>UNX</c>, <c>MIX</c>, …), the first sector and
/// the sector count. Bytes 0x1F6 and 0x1FA locate the bad-sector list; 0x1FE is a checksum that
/// only makes the sector bootable, so it is not required.
/// </para>
/// <para>
/// An <c>XGM</c> record opens the extended chain. Every link of the chain is an auxiliary root
/// sector laid out the same way: its first record is one data partition, whose start counts from
/// that auxiliary sector, and its second record, when it is <c>XGM</c>, points to the next
/// auxiliary sector, counted from the start of the first <c>XGM</c> partition.
/// </para>
/// <para>
/// The eight further records some ICD host-adapter drivers keep at 0x156 are not read.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>Atari Corp., AHDI 3.00 Release Notes (1990) — root sector layout, partition ids, the XGM chain.</description></item>
///   <item><description>GNU parted 3.6 <c>mklabel atari</c> and util-linux <c>partx</c> — black-box oracles for the offsets.</description></item>
/// </list>
/// </remarks>
public static class AtariAhdiParser {
  private const int SectorSize = 512;
  private const int DiskSizeOffset = 0x1C2;
  private const int RecordsOffset = 0x1C6;
  private const int RecordSize = 12;
  private const int RecordCount = 4;
  private const int MaxChainLength = 256;

  private readonly record struct Record(byte Flags, string Id, uint Start, uint Sectors) {
    public bool InUse => (this.Flags & 1) != 0;
    public bool IsExtended => this.Id == "XGM";
  }

  /// <summary>
  /// Reports whether <paramref name="sector0"/> is an AHDI root sector: a disk size that fits the
  /// image, and at least one record in use whose id is three letters or digits and whose extent
  /// lies inside that size — with every record in use passing the same test.
  /// </summary>
  public static bool IsAhdi(ReadOnlySpan<byte> sector0, long diskBytes) {
    if (sector0.Length < SectorSize)
      return false;
    var diskSectors = BinaryPrimitives.ReadUInt32BigEndian(sector0[DiskSizeOffset..]);
    if (diskSectors == 0 || (long)diskSectors * SectorSize > diskBytes)
      return false;
    var used = 0;
    for (var i = 0; i < RecordCount; ++i) {
      var r = ReadRecord(sector0, i);
      if (!r.InUse)
        continue;
      if (!IsValid(r, diskSectors))
        return false;
      ++used;
    }
    return used > 0;
  }

  private static readonly HashSet<string> KnownIds = ["GEM", "BGM", "XGM", "LNX", "SWP", "RAW", "UNX", "MIX", "MNX", "F32", "NBD"];

  /// <summary>
  /// The stricter test used to recognise an image by content alone: <see cref="IsAhdi"/>, and every
  /// record in use carries one of the ids AHDI and the operating systems sharing its disks use.
  /// </summary>
  public static bool IsKnownAhdi(ReadOnlySpan<byte> sector0, long diskBytes) {
    if (!IsAhdi(sector0, diskBytes))
      return false;
    for (var i = 0; i < RecordCount; ++i) {
      var r = ReadRecord(sector0, i);
      if (r.InUse && !KnownIds.Contains(r.Id))
        return false;
    }
    return true;
  }

  /// <summary>Lists the data partitions of the root sector and of the extended chain, in disk order.</summary>
  /// <exception cref="InvalidDataException">Sector 0 is not an AHDI root sector, or the chain is broken.</exception>
  public static List<PartitionEntry> Parse(Stream disk) {
    var sector = new byte[SectorSize];
    ReadSector(disk, 0, sector);
    if (!IsAhdi(sector, disk.Length))
      throw new InvalidDataException("Sector 0 is not an Atari AHDI root sector.");
    var diskSectors = BinaryPrimitives.ReadUInt32BigEndian(sector.AsSpan(DiskSizeOffset));

    var result = new List<PartitionEntry>();
    var roots = Enumerable.Range(0, RecordCount).Select(i => ReadRecord(sector, i)).ToList();
    foreach (var r in roots) {
      if (!r.InUse)
        continue;
      if (r.IsExtended)
        ReadChain(disk, r.Start, diskSectors, result);
      else
        result.Add(Entry(result.Count, r, r.Start, "AHDI"));
    }
    return result;
  }

  private static void ReadChain(Stream disk, uint firstXgm, uint diskSectors, List<PartitionEntry> result) {
    var sector = new byte[SectorSize];
    var link = firstXgm;
    var seen = new HashSet<uint>();
    for (var n = 0; ; ++n) {
      if (n >= MaxChainLength || !seen.Add(link))
        throw new InvalidDataException("Atari AHDI: the XGM chain loops or is too long.");
      if (link >= diskSectors)
        throw new InvalidDataException("Atari AHDI: an XGM link points past the disk.");
      ReadSector(disk, link, sector);
      var data = ReadRecord(sector, 0);
      if (data.InUse) {
        var start = (long)link + data.Start;
        if (!IsValid(data, diskSectors) || start + data.Sectors > diskSectors)
          throw new InvalidDataException("Atari AHDI: an extended partition lies outside the disk.");
        result.Add(Entry(result.Count, data, (uint)start, "XGM"));
      }
      var next = ReadRecord(sector, 1);
      if (!next.InUse || !next.IsExtended)
        return;
      link = firstXgm + next.Start;
    }
  }

  private static PartitionEntry Entry(int index, Record r, uint start, string source) => new() {
    Index = index,
    StartOffset = (long)start * SectorSize,
    Size = (long)r.Sectors * SectorSize,
    TypeName = TypeName(r.Id),
    TypeCode = r.Id,
    IsActive = (r.Flags & 0x80) != 0,
    Source = source,
  };

  private static Record ReadRecord(ReadOnlySpan<byte> sector, int i) {
    var p = sector[(RecordsOffset + i * RecordSize)..];
    var id = new string([(char)p[1], (char)p[2], (char)p[3]]);
    return new(p[0], id, BinaryPrimitives.ReadUInt32BigEndian(p[4..]), BinaryPrimitives.ReadUInt32BigEndian(p[8..]));
  }

  private static bool IsValid(Record r, uint diskSectors)
    => r.Id.All(char.IsAsciiLetterOrDigit) && r.Sectors > 0 && (ulong)r.Start + r.Sectors <= diskSectors;

  private static void ReadSector(Stream disk, long sector, byte[] buffer) {
    disk.Position = sector * SectorSize;
    if (disk.ReadAtLeast(buffer, SectorSize, throwOnEndOfStream: false) < SectorSize)
      throw new InvalidDataException("Atari AHDI: a table sector lies past the end of the image.");
  }

  private static string TypeName(string id) => id switch {
    "GEM" => "GEMDOS (up to 32 MB)",
    "BGM" => "GEMDOS (big)",
    "XGM" => "Extended",
    "LNX" => "Linux",
    "SWP" => "Linux swap",
    "RAW" => "Raw",
    "UNX" => "Atari System V",
    "MIX" => "Minix",
    "MNX" => "Minix",
    "F32" => "FAT32",
    _ => "Atari " + id,
  };
}
