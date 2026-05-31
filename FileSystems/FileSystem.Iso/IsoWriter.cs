#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Iso;

/// <summary>
/// Builds a minimal ISO 9660 (ECMA-119) disc image, optionally with a Joliet
/// Supplementary Volume Descriptor. File names passed to <see cref="AddFile"/>
/// may contain '/' separators; each separated segment becomes a real directory
/// in the on-disc directory-record tree (with its own extent, "." and ".."
/// records, and a matching path-table entry) rather than being flattened into
/// the root directory.
///
/// <para>When <see cref="EnableJoliet"/> is set (the default), the writer emits
/// a second, parallel directory tree carrying the original long, mixed-case,
/// Unicode names as UCS-2 (UTF-16) big-endian, described by a Supplementary
/// Volume Descriptor (type 2) with the UCS-2 level-3 escape sequence and its own
/// L/M path tables. Both trees reference the same shared file-data extents — only
/// the directory/name metadata differs: the primary tree carries short ECMA-119
/// (uppercase 8.3-ish, ";1") names, the Joliet tree the real long names.</para>
/// </summary>
public sealed class IsoWriter {
  private const int SectorSize = 2048;

  // Joliet name length limit: 64 UCS-2 characters (128 bytes) per the spec.
  private const int JolietMaxNameChars = 64;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Whether to emit a Joliet Supplementary Volume Descriptor and a parallel
  /// UCS-2 directory tree carrying the original long names. On by default.
  /// </summary>
  public bool EnableJoliet { get; set; } = true;

  /// <summary>
  /// Adds a file to the image. The name may contain '/' path separators, in
  /// which case the intermediate segments are created as directories.
  /// </summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  // ── Directory tree ───────────────────────────────────────────────────────

  private sealed class DirNode {
    public required string Name;            // ISO directory identifier (uppercase), "" for root
    public string JolietName = "";          // original (long, mixed-case) directory name, "" for root
    public DirNode? Parent;
    public readonly SortedDictionary<string, DirNode> Children =
      new(StringComparer.Ordinal);
    public readonly List<FileNode> Files = [];

    public int Lba;                         // primary-tree extent location
    public int Size;                        // primary-tree extent size in bytes
    public int PathTableIndex;              // 1-based index in the primary path table
    public int ParentPathTableIndex;        // 1-based parent index (root's parent = 1)

    public int JolietLba;                   // Joliet-tree extent location
    public int JolietSize;                  // Joliet-tree extent size in bytes
    public int JolietPathTableIndex;        // identical ordering, kept explicit for clarity
    public int JolietParentPathTableIndex;
  }

  private sealed class FileNode {
    public required string Identifier;      // ECMA-119 identifier, includes ";1" version suffix
    public required string JolietName;      // original (long, mixed-case) file name (truncated per spec)
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

    // Compute each directory's extent size for both trees.
    foreach (var dir in dirs) {
      dir.Size = CalculateDirectorySize(dir, joliet: false);
      if (this.EnableJoliet)
        dir.JolietSize = CalculateDirectorySize(dir, joliet: true);
    }

    // Reserved sectors 0-15 (system area), then the volume-descriptor set:
    //   16 PVD, [17 Joliet SVD,] then the terminator.
    var joliet = this.EnableJoliet;
    var svdLba = joliet ? 17 : -1;
    var terminatorLba = joliet ? 18 : 17;

    // Primary path tables follow the descriptor set.
    var pathTableSize = CalculatePathTableSize(dirs, joliet: false);
    var pathTableSectors = SectorsFor(pathTableSize);
    var lPathLba = terminatorLba + 1;
    var mPathLba = lPathLba + pathTableSectors;

    // Joliet path tables follow the primary ones.
    var cursor = mPathLba + pathTableSectors;
    var jolietPathTableSize = 0;
    var jolietLPathLba = 0;
    var jolietMPathLba = 0;
    if (joliet) {
      jolietPathTableSize = CalculatePathTableSize(dirs, joliet: true);
      var jolietPathTableSectors = SectorsFor(jolietPathTableSize);
      jolietLPathLba = cursor;
      jolietMPathLba = jolietLPathLba + jolietPathTableSectors;
      cursor = jolietMPathLba + jolietPathTableSectors;
    }

    // Primary directory extents (breadth-first, parents first).
    foreach (var dir in dirs) {
      dir.Lba = cursor;
      cursor += SectorsFor(dir.Size);
    }

    // Joliet directory extents follow the primary ones.
    if (joliet)
      foreach (var dir in dirs) {
        dir.JolietLba = cursor;
        cursor += SectorsFor(dir.JolietSize);
      }

    // Shared file data after all directory extents; referenced by both trees.
    foreach (var dir in dirs)
      foreach (var file in dir.Files) {
        file.Lba = cursor;
        var sectors = file.Data.Length == 0 ? 1 : (file.Data.Length + SectorSize - 1) / SectorSize;
        cursor += sectors;
      }

    var totalSectors = cursor;
    var image = new byte[totalSectors * SectorSize];

    // Primary Volume Descriptor (sector 16).
    WriteVolumeDescriptor(image, 16, type: 1, totalSectors, root,
      pathTableSize, lPathLba, mPathLba, rootLba: root.Lba, rootSize: root.Size, joliet: false);

    // Joliet Supplementary Volume Descriptor (sector 17), if enabled.
    if (joliet)
      WriteVolumeDescriptor(image, svdLba, type: 2, totalSectors, root,
        jolietPathTableSize, jolietLPathLba, jolietMPathLba,
        rootLba: root.JolietLba, rootSize: root.JolietSize, joliet: true);

    // Volume Descriptor Set Terminator.
    image[terminatorLba * SectorSize] = 0xFF;
    "CD001"u8.CopyTo(image.AsSpan(terminatorLba * SectorSize + 1));
    image[terminatorLba * SectorSize + 6] = 1;

    // Primary path tables (little- and big-endian copies).
    WritePathTable(image, lPathLba * SectorSize, dirs, littleEndian: true, joliet: false);
    WritePathTable(image, mPathLba * SectorSize, dirs, littleEndian: false, joliet: false);

    // Joliet path tables.
    if (joliet) {
      WritePathTable(image, jolietLPathLba * SectorSize, dirs, littleEndian: true, joliet: true);
      WritePathTable(image, jolietMPathLba * SectorSize, dirs, littleEndian: false, joliet: true);
    }

    // Directory extents (primary then Joliet).
    foreach (var dir in dirs)
      WriteDirectoryExtent(image, dir, joliet: false);
    if (joliet)
      foreach (var dir in dirs)
        WriteDirectoryExtent(image, dir, joliet: true);

    // Shared file data.
    foreach (var dir in dirs)
      foreach (var file in dir.Files)
        file.Data.CopyTo(image, file.Lba * SectorSize);

    return image;
  }

  private static int SectorsFor(int byteLength) {
    var sectors = (byteLength + SectorSize - 1) / SectorSize;
    return sectors < 1 ? 1 : sectors;
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
          child = new DirNode {
            Name = dirName,
            JolietName = TruncateJolietName(segments[i]),
            Parent = dir,
          };
          dir.Children.Add(dirName, child);
        }
        dir = child;
      }

      var identifier = NormalizeFileName(segments[^1]) + ";1";
      dir.Files.Add(new FileNode {
        Identifier = identifier,
        JolietName = TruncateJolietName(segments[^1]),
        Data = data,
      });
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
      dir.JolietPathTableIndex = dir.PathTableIndex;
      ordered.Add(dir);
      foreach (var child in dir.Children.Values)
        queue.Enqueue(child);
    }
    // Parent indices are resolved once every node has its own index.
    foreach (var dir in ordered) {
      dir.ParentPathTableIndex = dir.Parent?.PathTableIndex ?? 1; // root's parent = itself (1)
      dir.JolietParentPathTableIndex = dir.Parent?.JolietPathTableIndex ?? 1;
    }
    return ordered;
  }

  // ── Sizing ─────────────────────────────────────────────────────────────

  private static int CalculateDirectorySize(DirNode dir, bool joliet) {
    var size = 34 + 34; // "." and ".." records (single-byte identifier, 33 -> padded to 34)

    foreach (var child in dir.Children.Values)
      size = AppendRecord(size, IdentifierLength(joliet ? child.JolietName : child.Name, isFile: false, joliet));

    foreach (var file in dir.Files)
      size = AppendRecord(size, IdentifierLength(joliet ? file.JolietName : file.Identifier, isFile: true, joliet));

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

  // Byte length of a directory-record identifier. Joliet identifiers are UCS-2BE
  // (2 bytes per character); ECMA-119 identifiers are single-byte ASCII.
  private static int IdentifierLength(string name, bool isFile, bool joliet) =>
    joliet ? Encoding.BigEndianUnicode.GetByteCount(name)
           : Encoding.ASCII.GetByteCount(name);

  private static int CalculatePathTableSize(List<DirNode> dirs, bool joliet) {
    var size = 0;
    foreach (var dir in dirs) {
      // Root is identified by a single 0x00 byte (length 1); others by name.
      var nameLen = dir.Parent is null
        ? 1
        : (joliet ? Encoding.BigEndianUnicode.GetByteCount(dir.JolietName)
                  : Encoding.ASCII.GetByteCount(dir.Name));
      var recLen = 8 + nameLen;
      if ((recLen & 1) != 0) recLen++;
      size += recLen;
    }
    return size;
  }

  // ── Volume descriptor ────────────────────────────────────────────────────

  private static void WriteVolumeDescriptor(
      byte[] image, int sectorLba, byte type, int totalSectors, DirNode root,
      int pathTableSize, int lPathLba, int mPathLba, int rootLba, int rootSize, bool joliet) {
    var off = sectorLba * SectorSize;
    image[off] = type; // 1 = PVD, 2 = SVD
    "CD001"u8.CopyTo(image.AsSpan(off + 1));
    image[off + 6] = 1;

    // Joliet volume identifier is UCS-2BE; the primary one is ASCII.
    if (joliet) {
      // Escape sequence selecting UCS-2 level 3: 0x25 0x2F 0x45 ("%/E"), at offset 88.
      image[off + 88] = 0x25;
      image[off + 89] = 0x2F;
      image[off + 90] = 0x45;
      PadUcs2(image, off + 40, 32, "CDROM");
    } else {
      PadString(image, off + 40, 32, "CDROM");
    }

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

    // Root directory record (offset 156). Single-byte 0x00 identifier.
    WriteDirectoryRecord(image, off + 156, rootLba, rootSize, 0x02, [0]);
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

  private static void WriteDirectoryExtent(byte[] image, DirNode dir, bool joliet) {
    var lba = joliet ? dir.JolietLba : dir.Lba;
    var selfSize = joliet ? dir.JolietSize : dir.Size;
    var baseOff = lba * SectorSize;
    var pos = baseOff;

    // "." (self) and ".." (parent; root's parent is itself).
    var parent = dir.Parent ?? dir;
    var parentLba = joliet ? parent.JolietLba : parent.Lba;
    var parentSize = joliet ? parent.JolietSize : parent.Size;
    WriteDirectoryRecord(image, pos, lba, selfSize, 0x02, [0]);
    pos += image[pos];
    WriteDirectoryRecord(image, pos, parentLba, parentSize, 0x02, [1]);
    pos += image[pos];

    // Child directory records.
    foreach (var child in dir.Children.Values) {
      var identifier = DirectoryIdentifierBytes(child, joliet);
      var childLba = joliet ? child.JolietLba : child.Lba;
      var childSize = joliet ? child.JolietSize : child.Size;
      pos = AdvancePastSectorBoundary(baseOff, pos, identifier.Length);
      WriteDirectoryRecord(image, pos, childLba, childSize, 0x02, identifier);
      pos += image[pos];
    }

    // File records (both trees reference the same shared data extent).
    foreach (var file in dir.Files) {
      var identifier = FileIdentifierBytes(file, joliet);
      pos = AdvancePastSectorBoundary(baseOff, pos, identifier.Length);
      WriteDirectoryRecord(image, pos, file.Lba, file.Data.Length, 0x00, identifier);
      pos += image[pos];
    }
  }

  private static byte[] DirectoryIdentifierBytes(DirNode dir, bool joliet) =>
    joliet ? Encoding.BigEndianUnicode.GetBytes(dir.JolietName)
           : Encoding.ASCII.GetBytes(dir.Name);

  private static byte[] FileIdentifierBytes(FileNode file, bool joliet) =>
    joliet ? Encoding.BigEndianUnicode.GetBytes(file.JolietName)
           : Encoding.ASCII.GetBytes(file.Identifier);

  private static int AdvancePastSectorBoundary(int baseOff, int pos, int idLen) {
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;
    var sectorOffset = (pos - baseOff) % SectorSize;
    if (sectorOffset + recLen > SectorSize)
      pos += SectorSize - sectorOffset;
    return pos;
  }

  // ── Path table writer ──────────────────────────────────────────────────

  private static void WritePathTable(byte[] image, int offset, List<DirNode> dirs, bool littleEndian, bool joliet) {
    var pos = offset;
    foreach (var dir in dirs) {
      var isRoot = dir.Parent is null;
      var name = isRoot
        ? [0]
        : (joliet ? Encoding.BigEndianUnicode.GetBytes(dir.JolietName)
                  : Encoding.ASCII.GetBytes(dir.Name));
      var nameLen = name.Length;
      var lba = joliet ? dir.JolietLba : dir.Lba;
      var parentIndex = joliet ? dir.JolietParentPathTableIndex : dir.ParentPathTableIndex;

      image[pos] = (byte)nameLen;     // directory identifier length
      image[pos + 1] = 0;             // extended attribute record length

      if (littleEndian) {
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(pos + 2), (uint)lba);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pos + 6), (ushort)parentIndex);
      } else {
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos + 2), (uint)lba);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(pos + 6), (ushort)parentIndex);
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

  // Joliet caps identifiers at 64 UCS-2 characters (128 bytes). Longer names are
  // truncated to the limit while preserving the extension where possible.
  private static string TruncateJolietName(string name) {
    if (name.Length <= JolietMaxNameChars) return name;
    var dot = name.LastIndexOf('.');
    if (dot > 0 && name.Length - dot - 1 < JolietMaxNameChars) {
      var ext = name[dot..]; // includes the '.'
      var keep = JolietMaxNameChars - ext.Length;
      if (keep > 0) return name[..keep] + ext;
    }
    return name[..JolietMaxNameChars];
  }

  private static void PadString(byte[] image, int offset, int length, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    Array.Fill(image, (byte)0x20, offset, length);
    Array.Copy(bytes, 0, image, offset, Math.Min(bytes.Length, length));
  }

  // UCS-2BE padded field (Joliet a1/d1-characters fields are space-padded with
  // the UCS-2BE space 0x0020).
  private static void PadUcs2(byte[] image, int offset, int length, string value) {
    for (var i = 0; i + 1 < length; i += 2) {
      image[offset + i] = 0x00;
      image[offset + i + 1] = 0x20;
    }
    var bytes = Encoding.BigEndianUnicode.GetBytes(value);
    Array.Copy(bytes, 0, image, offset, Math.Min(bytes.Length, length));
  }
}
