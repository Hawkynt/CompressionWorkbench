#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.SmartFs;

/// <summary>
/// Describes where a SmartFS volume keeps its bytes, one sector at a time.
/// </summary>
/// <remarks>
/// <para>A file is a chain of sectors: the directory entry names the first, and
/// each sector's chain header names the one after it. A sector can therefore
/// sit anywhere the volume has room, and moving one means rewriting whichever
/// field named it — the entry, or the previous sector's next field.</para>
///
/// <para>The format sector and the root directory stay where they are: the
/// reader starts at logical sector three and works outwards, and the signature
/// is looked for in the first bytes of the volume.</para>
/// </remarks>
public static class SmartFsExtentMap {

  /// <summary>What one sector is, and what it belongs to.</summary>
  internal readonly record struct SectorInfo(
    int Sector, ushort Logical, ushort Next, byte ChainType, string Owner, bool Pinned);

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var volume = Read(image);
    if (volume == null) yield break;

    foreach (var sector in volume.Sectors) {
      var at = (long)sector.Sector * volume.SectorSize;
      yield return new DefragBlockInfo(at, volume.SectorSize,
        sector.Pinned ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
        sector.Owner);
    }
  }

  /// <summary>What a volume says about itself, and what each of its sectors holds.</summary>
  internal sealed class Volume {
    public int SectorSize { get; init; }
    public int TotalSectors { get; init; }
    public List<SectorInfo> Sectors { get; } = [];

    /// <summary>Where the field naming each sector lives, keyed by the sector it names.</summary>
    public Dictionary<int, long> PointedAtFrom { get; } = [];
  }

  /// <summary>Walks the volume, or returns null when it is not one this can read.</summary>
  internal static Volume? Read(Stream image) {
    if (!image.CanSeek || image.Length < 64) return null;

    var head = new byte[64];
    image.Position = 0;
    image.ReadExactly(head, 0, head.Length);

    var signature = "SMRT"u8;
    var at = -1;
    for (var i = 0; i + 6 <= head.Length; ++i)
      if (head.AsSpan(i, 4).SequenceEqual(signature)) { at = i; break; }
    if (at < 0) return null;

    var sectorSize = SectorSizeFromCode(head[at + 5]);
    if (sectorSize <= SmartFsLayout.SectorHeaderSize + SmartFsLayout.ChainHeaderSize) return null;
    if (image.Length % sectorSize != 0) return null;

    var volume = new Volume {
      SectorSize = sectorSize,
      TotalSectors = (int)(image.Length / sectorSize),
    };

    var raw = new byte[image.Length];
    image.Position = 0;
    image.ReadExactly(raw);

    // The root directory, and every directory chained from it, stays put: the
    // reader finds the root at a fixed logical sector, and a directory is
    // where a file's own first sector is named.
    var owners = new Dictionary<int, string>();
    var pinned = new HashSet<int> { 0, 1, 2, SmartFsLayout.RootDirSector };
    var payloadStart = SmartFsLayout.SectorHeaderSize + SmartFsLayout.ChainHeaderSize;

    var directories = new Queue<int>();
    directories.Enqueue(SmartFsLayout.RootDirSector);
    var seenDirectories = new HashSet<int>();
    while (directories.Count > 0) {
      var sector = directories.Dequeue();
      if (!seenDirectories.Add(sector)) continue;

      while (sector != SmartFsLayout.EndOfChain && sector < volume.TotalSectors) {
        pinned.Add(sector);
        var chain = raw.AsSpan(sector * sectorSize + SmartFsLayout.SectorHeaderSize);
        var next = BinaryPrimitives.ReadUInt16LittleEndian(chain);
        var used = BinaryPrimitives.ReadUInt16LittleEndian(chain[2..]);
        if (chain[4] != SmartFsLayout.ChainTypeDirectory) break;
        if (used > sectorSize - payloadStart) break;

        for (var offset = 0; offset + SmartFsLayout.EntrySize <= used; offset += SmartFsLayout.EntrySize) {
          var entryAt = sector * sectorSize + payloadStart + offset;
          var entry = raw.AsSpan(entryAt, SmartFsLayout.EntrySize);
          var flags = BinaryPrimitives.ReadUInt16LittleEndian(entry);
          if ((flags & SmartFsLayout.EntryActive) == 0) continue;

          var first = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
          var name = ReadName(entry[SmartFsLayout.EntryHeaderSize..]);
          if (name.Length == 0) continue;

          if ((flags & SmartFsLayout.EntryDirectory) != 0) { directories.Enqueue(first); continue; }

          // Follow the file's chain, noting the field that names each sector.
          volume.PointedAtFrom[first] = entryAt + 2;
          var link = first;
          var guard = new HashSet<int>();
          while (link != SmartFsLayout.EndOfChain && link < volume.TotalSectors && guard.Add(link)) {
            owners[link] = name;
            var linkChain = raw.AsSpan(link * sectorSize + SmartFsLayout.SectorHeaderSize);
            var linkNext = BinaryPrimitives.ReadUInt16LittleEndian(linkChain);
            if (linkNext != SmartFsLayout.EndOfChain && linkNext < volume.TotalSectors)
              volume.PointedAtFrom[linkNext] = link * sectorSize + SmartFsLayout.SectorHeaderSize;
            link = linkNext;
          }
        }

        if (next == SmartFsLayout.EndOfChain) break;
        sector = next;
      }
    }

    for (var sector = 0; sector < volume.TotalSectors; ++sector) {
      var logical = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(sector * sectorSize));
      var chain = raw.AsSpan(sector * sectorSize + SmartFsLayout.SectorHeaderSize);
      var next = BinaryPrimitives.ReadUInt16LittleEndian(chain);
      var type = chain[4];

      if (pinned.Contains(sector)) {
        volume.Sectors.Add(new SectorInfo(sector, logical, next, type,
          sector == 0 ? "SmartFS format sector" : "SmartFS directory", Pinned: true));
        continue;
      }

      // A sector no file claims is free — either never written, or left behind.
      if (!owners.TryGetValue(sector, out var owner)) continue;

      volume.Sectors.Add(new SectorInfo(sector, logical, next, type, owner, Pinned: false));
    }

    return volume;
  }

  private static string ReadName(ReadOnlySpan<byte> bytes) {
    var length = 0;
    while (length < bytes.Length && bytes[length] != 0) ++length;
    return Encoding.ASCII.GetString(bytes[..length]);
  }

  /// <summary>Sector size, as the format sector encodes it.</summary>
  internal static int SectorSizeFromCode(byte code) => code switch {
    0 => 256,
    1 => 512,
    2 => 1024,
    3 => 2048,
    4 => 4096,
    5 => 8192,
    6 => 16384,
    _ => 0,
  };
}
