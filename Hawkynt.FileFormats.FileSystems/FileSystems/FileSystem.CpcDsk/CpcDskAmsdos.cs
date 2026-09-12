#pragma warning disable CS1591
using System.Text;

namespace FileSystem.CpcDsk;

/// <summary>
/// The CP/M filesystem an Amstrad CPC keeps inside a DSK image.
/// </summary>
/// <remarks>
/// <para>A DSK file is a container for tracks and sectors; the files live in a
/// CP/M 2.2 filesystem laid over them, which AMSDOS calls the DATA format. Its
/// parameters are not negotiable, because they are what the machine's own ROM
/// assumes: one-kilobyte allocation blocks numbered from the very start of the
/// disk, the directory in blocks zero and one, sixty-four entries, and no
/// reserved system tracks. A disk that numbers its blocks any other way has a
/// directory a CPC reads as pointing somewhere else entirely.</para>
///
/// <para>References:
/// <list type="bullet">
///   <item><description><c>https://www.cpcwiki.eu/index.php/Format:DSK_disk_image_file_format</c> — the container</description></item>
///   <item><description><c>https://www.cpcwiki.eu/index.php/AMSDOS</c> — the DATA format's disk parameter block</description></item>
///   <item><description>Digital Research, <i>CP/M 2.2 Alteration Guide</i> — the directory entry and how extents chain</description></item>
/// </list></para>
/// </remarks>
internal static class CpcDskAmsdos {

  internal const int SectorSize = 512;
  internal const int SectorsPerTrack = 9;

  /// <summary>DATA-format sector IDs run &amp;C1 upward; the ROM looks for them by ID.</summary>
  internal const byte FirstSectorId = 0xC1;

  internal const int DiskInfoSize = 256;
  internal const int TrackInfoSize = 256;

  /// <summary>An allocation block is a kilobyte: two sectors, not one.</summary>
  internal const int BlockSize = 1024;
  internal const int SectorsPerBlock = BlockSize / SectorSize;

  internal const int DirEntrySize = 32;

  /// <summary>The directory is blocks zero and one, and the data starts after it.</summary>
  internal const int DirectoryBlocks = 2;
  internal const int DirectoryEntries = DirectoryBlocks * BlockSize / DirEntrySize;
  internal const int FirstDataBlock = DirectoryBlocks;

  /// <summary>A CP/M record is 128 bytes, and a file's length is only ever a count of them.</summary>
  internal const int RecordSize = 128;

  /// <summary>One directory entry covers sixteen blocks, so sixteen kilobytes.</summary>
  internal const int BlocksPerExtent = 16;
  internal const int RecordsPerExtent = BlockSize * BlocksPerExtent / RecordSize;

  /// <summary>What CP/M writes over a directory entry that is not in use.</summary>
  internal const byte Unused = 0xE5;

  /// <summary>Where the tracks are, and how to turn a block number into a byte offset.</summary>
  internal sealed class Geometry {
    internal required int Tracks { get; init; }
    internal required int Sides { get; init; }
    internal required int SectorsPerTrackCount { get; init; }
    internal required int SectorBytes { get; init; }

    /// <summary>Byte offset of each (track, side) block within the image, or -1 when absent.</summary>
    internal required long[] TrackOffsets { get; init; }

    internal int SectorsPerCylinder => this.SectorsPerTrackCount * this.Sides;

    /// <summary>Every block the disk has, directory included.</summary>
    internal int TotalBlocks =>
      this.Tracks * this.SectorsPerCylinder * this.SectorBytes / BlockSize;

    /// <summary>The blocks a file may actually be given.</summary>
    internal int DataBlocks => Math.Max(0, this.TotalBlocks - DirectoryBlocks);

    /// <summary>
    /// A DSK laid out the ordinary way: the disk info header, then each track's
    /// info block followed by its sectors.
    /// </summary>
    internal static Geometry Standard(int tracks, int sides,
        int sectorsPerTrack = SectorsPerTrack, int sectorBytes = SectorSize) {
      var offsets = new long[tracks * sides];
      var at = (long)DiskInfoSize;
      var trackBytes = TrackInfoSize + (long)sectorsPerTrack * sectorBytes;
      for (var t = 0; t < tracks; ++t)
        for (var s = 0; s < sides; ++s) {
          offsets[t * sides + s] = at;
          at += trackBytes;
        }

      return new Geometry {
        Tracks = tracks, Sides = sides,
        SectorsPerTrackCount = sectorsPerTrack, SectorBytes = sectorBytes,
        TrackOffsets = offsets,
      };
    }

    /// <summary>How long such an image is.</summary>
    internal long ImageLength =>
      DiskInfoSize + (long)this.Tracks * this.Sides
        * (TrackInfoSize + (long)this.SectorsPerTrackCount * this.SectorBytes);

    /// <summary>
    /// Where one logical sector sits in the image. Logical order is the order the
    /// ROM reads sectors in: by ascending id within a track, side by side, track
    /// by track — which is what makes a block number mean the same thing here as
    /// it does on the machine.
    /// </summary>
    internal long SectorOffset(int logicalSector) {
      if (logicalSector < 0) return -1;

      var cylinder = logicalSector / this.SectorsPerCylinder;
      var withinCylinder = logicalSector % this.SectorsPerCylinder;
      var side = withinCylinder / this.SectorsPerTrackCount;
      var index = withinCylinder % this.SectorsPerTrackCount;
      if (cylinder >= this.Tracks || side >= this.Sides) return -1;

      var trackBase = this.TrackOffsets[cylinder * this.Sides + side];
      return trackBase < 0 ? -1 : trackBase + TrackInfoSize + (long)index * this.SectorBytes;
    }

    /// <summary>The sectors one allocation block is made of.</summary>
    internal IEnumerable<int> SectorsOfBlock(int block) {
      var first = block * (BlockSize / this.SectorBytes);
      for (var i = 0; i < BlockSize / this.SectorBytes; ++i) yield return first + i;
    }
  }

  /// <summary>One file, as the directory describes it.</summary>
  internal sealed class AmsdosFile {
    internal required string Name { get; init; }
    internal required byte User { get; init; }

    /// <summary>Blocks in file order, which is how the extents chain them.</summary>
    internal required List<int> Blocks { get; init; }

    /// <summary>Records, so the length in bytes is this times 128.</summary>
    internal required int Records { get; set; }

    internal long Length => (long)this.Records * RecordSize;
  }

  /// <summary>Strips the attribute bits CP/M keeps in the top bit of each name byte.</summary>
  /// <remarks>
  /// A name is padded with spaces, but a disk that has merely been formatted has
  /// a directory of zero bytes, and a zero is not a space. Trimming only spaces
  /// read those eight nulls as an eight-character name, so a blank disk came back
  /// holding a file with an unprintable name and whatever bytes block zero held.
  /// </remarks>
  internal static string CleanName(ReadOnlySpan<byte> raw) {
    Span<char> chars = stackalloc char[raw.Length];
    for (var i = 0; i < raw.Length; ++i) chars[i] = (char)(raw[i] & 0x7F);
    return new string(chars).TrimEnd(' ', '\0');
  }

  /// <summary>Splits a name into the eight and three characters CP/M has room for.</summary>
  internal static (string Base, string Extension) SplitName(string name) {
    var leaf = name.Replace('\\', '/');
    var slash = leaf.LastIndexOf('/');
    if (slash >= 0) leaf = leaf[(slash + 1)..];

    var dot = leaf.LastIndexOf('.');
    var basePart = dot >= 0 ? leaf[..dot] : leaf;
    var extPart = dot >= 0 ? leaf[(dot + 1)..] : "";

    static string Clean(string s, int width) {
      var buffer = new StringBuilder(width);
      foreach (var c in s.ToUpperInvariant()) {
        if (buffer.Length == width) break;
        // CP/M has no room for the characters that separate names from one another.
        buffer.Append(c is < ' ' or '<' or '>' or '.' or ',' or ';' or ':' or '=' or '?'
          or '*' or '[' or ']' or '/' or '\\' or '|' or '(' or ')' ? '_' : c);
      }
      return buffer.ToString().PadRight(width);
    }

    return (Clean(basePart, 8), Clean(extPart, 3));
  }

  /// <summary>Joins the two halves back into the name a caller sees.</summary>
  internal static string JoinName(string basePart, string extension) {
    var b = basePart.TrimEnd();
    var x = extension.TrimEnd();
    return x.Length == 0 ? b : b + "." + x;
  }

  /// <summary>
  /// Reads the directory out of a disk image and returns the files it names.
  /// </summary>
  /// <remarks>
  /// A file longer than sixteen kilobytes needs more than one directory entry, so
  /// the entries are gathered by name and put back in extent order; the record
  /// count of each says how much of its last block is really the file.
  /// </remarks>
  internal static List<AmsdosFile> ReadDirectory(byte[] image, Geometry geometry) {
    var directory = ReadDirectoryBytes(image, geometry);
    var byName = new Dictionary<string, AmsdosFile>(StringComparer.Ordinal);
    var order = new List<string>();
    var extents = new List<(string Key, int Extent, int Records, List<int> Blocks, byte User, string Name)>();

    for (var slot = 0; slot < DirectoryEntries; ++slot) {
      var at = slot * DirEntrySize;
      if (at + DirEntrySize > directory.Length) break;

      var user = directory[at];
      if (user > 0x0F) continue;                       // 0xE5 is free; anything else is not a file

      var basePart = CleanName(directory.AsSpan(at + 1, 8));
      var extPart = CleanName(directory.AsSpan(at + 9, 3));
      if (basePart.Length == 0 && extPart.Length == 0) continue;

      var extentNumber = directory[at + 12] + directory[at + 14] * 32;
      var records = directory[at + 15];
      var blocks = new List<int>();
      for (var b = 0; b < BlocksPerExtent; ++b) {
        var block = directory[at + 16 + b];
        if (block == 0) continue;                      // block zero is the directory, so it means "none"
        blocks.Add(block);
      }

      var name = JoinName(basePart, extPart);
      extents.Add(($"{user}:{name}", extentNumber, records, blocks, user, name));
    }

    foreach (var extent in extents.OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Extent)) {
      if (!byName.TryGetValue(extent.Key, out var file)) {
        file = new AmsdosFile {
          Name = extent.Name, User = extent.User, Blocks = [], Records = 0,
        };
        byName[extent.Key] = file;
        order.Add(extent.Key);
      }

      file.Blocks.AddRange(extent.Blocks);
      file.Records += extent.Records;
    }

    return order.Select(k => byName[k]).ToList();
  }

  /// <summary>The directory's own bytes, gathered from the blocks holding it.</summary>
  internal static byte[] ReadDirectoryBytes(byte[] image, Geometry geometry) {
    var directory = new byte[DirectoryBlocks * BlockSize];
    var at = 0;
    for (var block = 0; block < DirectoryBlocks; ++block)
      foreach (var sector in geometry.SectorsOfBlock(block)) {
        var offset = geometry.SectorOffset(sector);
        if (offset >= 0 && offset + geometry.SectorBytes <= image.Length)
          Array.Copy(image, offset, directory, at, geometry.SectorBytes);
        at += geometry.SectorBytes;
      }

    return directory;
  }

  /// <summary>
  /// Lays a set of files out as directory entries, or says why they will not fit.
  /// </summary>
  /// <remarks>
  /// Blocks are handed out in order from the first one past the directory. A file
  /// takes one entry per sixteen kilobytes, and every entry it takes is one fewer
  /// the disk has: running out of entries is as real a limit as running out of
  /// room, and a disk that quietly dropped the files it could not describe would
  /// read back short with nothing to say about it.
  /// </remarks>
  internal static byte[] BuildDirectory(IReadOnlyList<(string Name, byte[] Data)> files,
      Geometry geometry, out Dictionary<string, List<int>> placement) {
    var directory = new byte[DirectoryBlocks * BlockSize];
    Array.Fill(directory, Unused);

    placement = new Dictionary<string, List<int>>(StringComparer.Ordinal);
    var nextBlock = FirstDataBlock;
    var slot = 0;

    foreach (var (name, data) in files) {
      var (basePart, extPart) = SplitName(name);
      var blocksNeeded = Math.Max(1, (int)(((long)data.Length + BlockSize - 1) / BlockSize));
      var records = Math.Max(1, (int)(((long)data.Length + RecordSize - 1) / RecordSize));

      if (nextBlock + blocksNeeded > geometry.TotalBlocks)
        throw new InvalidOperationException(
          $"CPC DSK: '{name}' needs {blocksNeeded} block(s) of {BlockSize} bytes but the disk has "
          + $"{Math.Max(0, geometry.TotalBlocks - nextBlock)} left of {geometry.DataBlocks}.");

      var assigned = new List<int>(blocksNeeded);
      for (var b = 0; b < blocksNeeded; ++b) assigned.Add(nextBlock + b);
      nextBlock += blocksNeeded;
      placement[name] = assigned;

      // One entry per sixteen blocks, each carrying its own share of the records.
      var written = 0;
      var extentNumber = 0;
      while (written < blocksNeeded) {
        if (slot >= DirectoryEntries)
          throw new InvalidOperationException(
            $"CPC DSK: the directory holds {DirectoryEntries} entries and '{name}' needs one more; "
            + "a file over sixteen kilobytes takes an entry for every sixteen.");

        var take = Math.Min(BlocksPerExtent, blocksNeeded - written);
        var recordsBefore = extentNumber * RecordsPerExtent;
        var recordsHere = Math.Min(RecordsPerExtent, records - recordsBefore);

        var at = slot * DirEntrySize;
        // The whole entry is cleared first. The free-entry filler is 0xE5, and an
        // allocation slot left holding it is not an empty slot — it is block 229,
        // which CP/M would follow to bytes belonging to some other file. Unused
        // allocation slots are zero, and zero is what "no block" means.
        Array.Clear(directory, at, DirEntrySize);
        directory[at] = 0;                                                   // user zero
        Encoding.ASCII.GetBytes(basePart).CopyTo(directory, at + 1);
        Encoding.ASCII.GetBytes(extPart).CopyTo(directory, at + 9);
        directory[at + 12] = (byte)(extentNumber & 0x1F);                    // EX
        directory[at + 13] = 0;                                              // S1
        directory[at + 14] = (byte)(extentNumber >> 5);                      // S2
        directory[at + 15] = (byte)Math.Clamp(recordsHere, 0, RecordsPerExtent);
        for (var b = 0; b < take; ++b)
          directory[at + 16 + b] = (byte)assigned[written + b];

        written += take;
        ++extentNumber;
        ++slot;
      }
    }

    return directory;
  }
}
