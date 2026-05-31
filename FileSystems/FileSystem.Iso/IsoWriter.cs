#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Iso;

/// <summary>
/// Builds a minimal ISO 9660 (ECMA-119) disc image. File names passed to
/// <see cref="AddFile"/> may contain '/' separators; each separated segment
/// becomes a real directory in the on-disc directory-record tree (with its own
/// extent, "." and ".." records, and a matching path-table entry) rather than
/// being flattened into the root directory.
/// </summary>
public sealed class IsoWriter {
  private const int SectorSize = 2048;
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Adds a file to the image. The name may contain '/' path separators, in
  /// which case the intermediate segments are created as directories.
  /// </summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  // ── Directory tree ───────────────────────────────────────────────────────

  private sealed class DirNode {
    public required string Name;            // ISO directory identifier (uppercase), "" for root
    public DirNode? Parent;
    public readonly SortedDictionary<string, DirNode> Children =
      new(StringComparer.Ordinal);
    public readonly List<FileNode> Files = [];

    public int Lba;                         // assigned extent location
    public int Size;                        // extent size in bytes
    public int PathTableIndex;              // 1-based index in the path table
    public int ParentPathTableIndex;        // 1-based parent index (root's parent = 1)
  }

  private sealed class FileNode {
    public required string Identifier;      // includes ";1" version suffix
    public required byte[] Data;
    public int Lba;
  }

  /// <summary>
  /// Builds the complete ISO 9660 image and returns it as a byte array.
  /// </summary>
  public byte[] Build() {
    var root = BuildTree();

    // Enumerate directories in breadth-first order: a node's parent always
    // precedes it, which is exactly the ordering the path table requires.
    var dirs = OrderDirectoriesBreadthFirst(root);

    // Compute each directory's extent size from its records.
    foreach (var dir in dirs)
      dir.Size = CalculateDirectorySize(dir);

    // Reserved sectors: 0-15 system area, 16 PVD, 17 terminator,
    // 18 L path table, 19 M path table. Directory extents start at 20.
    const int FirstDirSector = 20;

    // Path table size (one entry per directory; both copies share this size).
    var pathTableSize = CalculatePathTableSize(dirs);
    var pathTableSectors = (pathTableSize + SectorSize - 1) / SectorSize;
    if (pathTableSectors < 1) pathTableSectors = 1;

    // The single-sector L/M path table layout assumes the table fits in one
    // sector; widen the reserved span if it grows beyond that.
    var lPathLba = 18;
    var mPathLba = 18 + pathTableSectors;
    var firstDirSector = mPathLba + pathTableSectors;
    if (firstDirSector < FirstDirSector) firstDirSector = FirstDirSector;

    // Assign directory extent LBAs (breadth-first, parents first).
    var cursor = firstDirSector;
    foreach (var dir in dirs) {
      dir.Lba = cursor;
      cursor += (dir.Size + SectorSize - 1) / SectorSize;
    }

    // Assign file data LBAs after all directory extents.
    foreach (var dir in dirs)
      foreach (var file in dir.Files) {
        file.Lba = cursor;
        var sectors = (file.Data.Length + SectorSize - 1) / SectorSize;
        if (file.Data.Length == 0) sectors = 1; // empty file still gets a sector
        cursor += sectors;
      }

    var totalSectors = cursor;
    var image = new byte[totalSectors * SectorSize];

    // Sector 16: Primary Volume Descriptor.
    WritePvd(image, totalSectors, root, pathTableSize, lPathLba, mPathLba);

    // Sector 17: Volume Descriptor Set Terminator.
    image[17 * SectorSize] = 0xFF;
    "CD001"u8.CopyTo(image.AsSpan(17 * SectorSize + 1));
    image[17 * SectorSize + 6] = 1;

    // Path tables (little- and big-endian copies).
    WritePathTable(image, lPathLba * SectorSize, dirs, littleEndian: true);
    WritePathTable(image, mPathLba * SectorSize, dirs, littleEndian: false);

    // Directory extents.
    foreach (var dir in dirs)
      WriteDirectoryExtent(image, dir);

    // File data.
    foreach (var dir in dirs)
      foreach (var file in dir.Files)
        file.Data.CopyTo(image, file.Lba * SectorSize);

    return image;
  }

  private DirNode BuildTree() {
    var root = new DirNode { Name = "" };
    foreach (var (rawName, data) in _files) {
      var segments = rawName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      var dir = root;
      for (var i = 0; i < segments.Length - 1; i++) {
        var dirName = NormalizeDirectoryName(segments[i]);
        if (!dir.Children.TryGetValue(dirName, out var child)) {
          child = new DirNode { Name = dirName, Parent = dir };
          dir.Children.Add(dirName, child);
        }
        dir = child;
      }

      var identifier = NormalizeFileName(segments[^1]) + ";1";
      dir.Files.Add(new FileNode { Identifier = identifier, Data = data });
    }
    return root;
  }

  private static List<DirNode> OrderDirectoriesBreadthFirst(DirNode root) {
    var ordered = new List<DirNode>();
    var queue = new Queue<DirNode>();
    queue.Enqueue(root);
    while (queue.Count > 0) {
      var dir = queue.Dequeue();
      dir.PathTableIndex = ordered.Count + 1; // 1-based
      ordered.Add(dir);
      foreach (var child in dir.Children.Values)
        queue.Enqueue(child);
    }
    // Parent indices are resolved once every node has its own index.
    foreach (var dir in ordered)
      dir.ParentPathTableIndex = dir.Parent?.PathTableIndex ?? 1; // root's parent = itself (1)
    return ordered;
  }

  // ── Sizing ─────────────────────────────────────────────────────────────

  private static int CalculateDirectorySize(DirNode dir) {
    var size = 34 + 34; // "." and ".." records

    foreach (var child in dir.Children.Values)
      size = AppendRecord(size, DirectoryIdentifierLength(child.Name));

    foreach (var file in dir.Files)
      size = AppendRecord(size, Encoding.ASCII.GetByteCount(file.Identifier));

    if (size % SectorSize != 0)
      size += SectorSize - (size % SectorSize);
    if (size == 0) size = SectorSize;
    return size;
  }

  private static int AppendRecord(int size, int idLen) {
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;
    var used = size % SectorSize;
    if (used + recLen > SectorSize)
      size += SectorSize - used; // a record may not span a sector boundary
    return size + recLen;
  }

  private static int DirectoryIdentifierLength(string name) =>
    Encoding.ASCII.GetByteCount(name);

  private static int CalculatePathTableSize(List<DirNode> dirs) {
    var size = 0;
    foreach (var dir in dirs) {
      // Root is identified by a single 0x00 byte (length 1); others by name.
      var nameLen = dir.Parent is null ? 1 : Encoding.ASCII.GetByteCount(dir.Name);
      var recLen = 8 + nameLen;
      if ((recLen & 1) != 0) recLen++;
      size += recLen;
    }
    return size;
  }

  // ── Volume descriptor ────────────────────────────────────────────────────

  private static void WritePvd(byte[] image, int totalSectors, DirNode root, int pathTableSize, int lPathLba, int mPathLba) {
    var off = 16 * SectorSize;
    image[off] = 1; // type = PVD
    "CD001"u8.CopyTo(image.AsSpan(off + 1));
    image[off + 6] = 1;

    PadString(image, off + 40, 32, "CDROM");

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 80), (uint)totalSectors);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 84), (uint)totalSectors);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 120), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 122), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 124), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 126), 1);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 128), SectorSize);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 130), SectorSize);

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 132), (uint)pathTableSize);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 136), (uint)pathTableSize);

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 140), (uint)lPathLba);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 148), (uint)mPathLba);

    // Root directory record (offset 156).
    WriteDirectoryRecord(image, off + 156, root.Lba, root.Size, 0x02, [0]);
  }

  // ── Directory record / extent writers ─────────────────────────────────────

  private static void WriteDirectoryRecord(byte[] image, int off, int lba, int size, byte flags, byte[] identifier) {
    var idLen = identifier.Length;
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;

    image[off] = (byte)recLen;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 2), (uint)lba);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 6), (uint)lba);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 10), (uint)size);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 14), (uint)size);

    var now = DateTime.UtcNow;
    image[off + 18] = (byte)(now.Year - 1900);
    image[off + 19] = (byte)now.Month;
    image[off + 20] = (byte)now.Day;
    image[off + 21] = (byte)now.Hour;
    image[off + 22] = (byte)now.Minute;
    image[off + 23] = (byte)now.Second;
    image[off + 24] = 0;

    image[off + 25] = flags;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 28), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 30), 1);

    image[off + 32] = (byte)idLen;
    identifier.CopyTo(image, off + 33);
  }

  private static void WriteDirectoryExtent(byte[] image, DirNode dir) {
    var baseOff = dir.Lba * SectorSize;
    var pos = baseOff;

    // "." (self) and ".." (parent; root's parent is itself).
    var parent = dir.Parent ?? dir;
    WriteDirectoryRecord(image, pos, dir.Lba, dir.Size, 0x02, [0]);
    pos += image[pos];
    WriteDirectoryRecord(image, pos, parent.Lba, parent.Size, 0x02, [1]);
    pos += image[pos];

    // Child directory records.
    foreach (var child in dir.Children.Values) {
      var identifier = Encoding.ASCII.GetBytes(child.Name);
      pos = AdvancePastSectorBoundary(baseOff, pos, identifier.Length);
      WriteDirectoryRecord(image, pos, child.Lba, child.Size, 0x02, identifier);
      pos += image[pos];
    }

    // File records.
    foreach (var file in dir.Files) {
      var identifier = Encoding.ASCII.GetBytes(file.Identifier);
      pos = AdvancePastSectorBoundary(baseOff, pos, identifier.Length);
      WriteDirectoryRecord(image, pos, file.Lba, file.Data.Length, 0x00, identifier);
      pos += image[pos];
    }
  }

  private static int AdvancePastSectorBoundary(int baseOff, int pos, int idLen) {
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;
    var sectorOffset = (pos - baseOff) % SectorSize;
    if (sectorOffset + recLen > SectorSize)
      pos += SectorSize - sectorOffset;
    return pos;
  }

  // ── Path table writer ──────────────────────────────────────────────────

  private static void WritePathTable(byte[] image, int offset, List<DirNode> dirs, bool littleEndian) {
    var pos = offset;
    foreach (var dir in dirs) {
      var isRoot = dir.Parent is null;
      var name = isRoot ? [0] : Encoding.ASCII.GetBytes(dir.Name);
      var nameLen = name.Length;

      image[pos] = (byte)nameLen;     // directory identifier length
      image[pos + 1] = 0;             // extended attribute record length

      if (littleEndian) {
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(pos + 2), (uint)dir.Lba);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pos + 6), (ushort)dir.ParentPathTableIndex);
      } else {
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos + 2), (uint)dir.Lba);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(pos + 6), (ushort)dir.ParentPathTableIndex);
      }

      name.CopyTo(image, pos + 8);
      var recLen = 8 + nameLen;
      if ((recLen & 1) != 0) recLen++; // pad to even length
      pos += recLen;
    }
  }

  // ── Name normalization ─────────────────────────────────────────────────

  private static string NormalizeFileName(string name) => name.ToUpperInvariant();

  private static string NormalizeDirectoryName(string name) => name.ToUpperInvariant();

  private static void PadString(byte[] image, int offset, int length, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    Array.Fill(image, (byte)0x20, offset, length);
    Array.Copy(bytes, 0, image, offset, Math.Min(bytes.Length, length));
  }
}
