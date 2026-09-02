#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Adfs;

/// <summary>
/// Describes where an old-map ADFS disc keeps its bytes: the two free-space
/// map sectors, the root directory, and each file's run of sectors.
/// </summary>
/// <remarks>
/// <para>An old-map ADFS file is one contiguous run. Its directory entry
/// carries the sector it starts at and its length in bytes, which is the whole
/// of what says where it is — so a run can be moved and the entry rewritten.</para>
///
/// <para>New-map discs are not described here. There a file is a fragment
/// identifier resolved through a zone bitmap, and neither the fragment's
/// position nor its length is written down anywhere a move could rewrite.</para>
/// </remarks>
public static class AdfsExtentMap {

  internal const int SectorSize = 256;

  /// <summary>Sectors the free-space map occupies, from the start of the disc.</summary>
  internal const int FreeMapSectors = 2;

  /// <summary>Where the root directory starts, and how far it runs.</summary>
  internal const int RootDirectorySector = 2;

  internal const int RootDirectorySectors = 5;

  /// <summary>First sector a file may occupy.</summary>
  internal const int FirstDataSector = RootDirectorySector + RootDirectorySectors;

  private const int DirectoryEntriesOffset = 5;
  private const int DirectoryEntrySize = 26;
  private const int MaxDirectoryEntries = 47;

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    if (!IsOldMap(image)) {
      foreach (var block in EnumerateNewMap(image)) yield return block;
      yield break;
    }

    yield return new DefragBlockInfo(0, (long)FreeMapSectors * SectorSize,
      DefragBlockKind.MetadataReserved, "ADFS free space map");
    yield return new DefragBlockInfo((long)RootDirectorySector * SectorSize,
      (long)RootDirectorySectors * SectorSize,
      DefragBlockKind.MetadataReserved, "ADFS root directory");

    foreach (var (name, start, length, _) in Files(image)) {
      var at = (long)start * SectorSize;
      if (length <= 0 || at + length > image.Length) continue;
      yield return new DefragBlockInfo(at, length, DefragBlockKind.Used, name);
    }
  }

  /// <summary>
  /// Where a new-map disc keeps its bytes. A file is a fragment, and the run of
  /// bits carrying its identifier in the zone bitmap is what says where that
  /// fragment sits — so the extent is the fragment, slack and all.
  /// </summary>
  private static IEnumerable<DefragBlockInfo> EnumerateNewMap(Stream image) {
    var layout = AdfsNewMap.TryRead(image);
    if (layout == null) yield break;

    var names = NewMapNames(image, layout);
    foreach (var (id, first, sectors) in layout.Fragments) {
      var at = (long)first * layout.SectorSize;
      var length = (long)sectors * layout.SectorSize;
      if (at + length > image.Length) continue;

      if (id is AdfsNewMap.MapFragment or AdfsNewMap.RootFragment) {
        yield return new DefragBlockInfo(at, length, DefragBlockKind.MetadataReserved,
          id == AdfsNewMap.MapFragment ? "ADFS map zone" : "ADFS root directory");
        continue;
      }

      // Identifier zero is free space, threaded onto the zone's free chain.
      if (id == 0) continue;

      yield return new DefragBlockInfo(at, length, DefragBlockKind.Used,
        names.TryGetValue(id, out var name) ? name : $"fragment {id}");
    }
  }

  /// <summary>Which fragment each file in the root directory is.</summary>
  internal static Dictionary<uint, string> NewMapNames(Stream image, AdfsNewMap.Layout layout) {
    var names = new Dictionary<uint, string>();
    var root = layout.Fragments.FirstOrDefault(f => f.Id == AdfsNewMap.RootFragment);
    if (root.Sectors == 0) return names;

    var size = layout.RootSize == 0 ? 2048 : layout.RootSize;
    var at = (long)root.FirstSector * layout.SectorSize;
    if (at + size > image.Length) return names;

    var directory = new byte[size];
    image.Position = at;
    image.ReadExactly(directory);

    const int firstEntry = 5;
    const int entrySize = 26;
    const int maxEntries = 77;
    for (var i = 0; i < maxEntries; ++i) {
      var off = firstEntry + i * entrySize;
      if (off + entrySize > directory.Length || directory[off] == 0) break;

      var nameLength = 0;
      while (nameLength < 10 && directory[off + nameLength] >= 0x20) ++nameLength;
      var indirect = (uint)(directory[off + 22] | (directory[off + 23] << 8) | (directory[off + 24] << 16));
      names[indirect >> 8] = Encoding.ASCII.GetString(directory, off, nameLength);
    }

    return names;
  }

  /// <summary>Whether this disc carries the old map rather than the new one.</summary>
  internal static bool IsOldMap(Stream image) {
    if (!image.CanSeek || image.Length < (long)FirstDataSector * SectorSize) return false;

    var position = image.Position;
    try {
      image.Position = 0;
      using var reader = new AdfsReader(image);
      return !reader.IsNewMap;
    } catch {
      return false;
    } finally {
      image.Position = position;
    }
  }

  /// <summary>
  /// Every root directory entry: its name, the sector it starts at, its length
  /// in bytes, and where in the directory the entry itself sits.
  /// </summary>
  internal static List<(string Name, uint StartSector, long Length, int EntryOffset)> Files(Stream image) {
    var files = new List<(string, uint, long, int)>();
    var directory = AdfsModifier.ReadDirectory(image);

    for (var i = 0; i < MaxDirectoryEntries; ++i) {
      var at = DirectoryEntriesOffset + i * DirectoryEntrySize;
      if (at + DirectoryEntrySize > directory.Length) break;
      if ((directory[at] & 0x7F) == 0) break;                 // the end-of-directory sentinel

      var name = new StringBuilder();
      for (var c = 0; c < 10; ++c) {
        var ch = (char)(directory[at + c] & 0x7F);
        if (ch is '\0' or '\r') break;
        name.Append(ch);
      }

      var length = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 0x12, 4));
      var start = AdfsModifier.ReadUInt24LittleEndian(directory.AsSpan(at + 0x16, 3));
      files.Add((name.ToString(), start, length, at));
    }

    return files;
  }
}
