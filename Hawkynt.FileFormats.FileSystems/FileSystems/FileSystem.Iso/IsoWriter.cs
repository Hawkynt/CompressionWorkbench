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

  // Trailing zero-sector padding appended after the file data. mkisofs/genisoimage
  // append a 150-sector (300 KiB) post-gap to every image by default; cdrtools
  // isoinfo refuses to open an image whose backing file is shorter than its own
  // fixed start-up read window (~48 sectors) and reports "Short read on old image"
  // regardless of how well-formed the descriptors are. Matching the mkisofs
  // convention keeps the image readable by isoinfo and any reader that relies on
  // the post-gap, while the recorded Volume Space Size still spans the whole file.
  private const int TrailingPadSectors = 150;

  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every streaming file's
  /// (absolute data-extent byte offset, size, opener) is appended for use by
  /// <see cref="BuildToStreaming"/>'s post-stream pass. When null, the writer
  /// behaves identically to before.
  /// </summary>
  private List<(long ByteOffset, long Size, Func<Stream> Opener)>? _streamingSink;

  /// <summary>
  /// Whether to emit a Joliet Supplementary Volume Descriptor and a parallel
  /// UCS-2 directory tree carrying the original long names. On by default.
  /// </summary>
  public bool EnableJoliet { get; set; } = true;

  /// <summary>
  /// ECMA-119 8.4.7 Volume Identifier (d-characters, 32 bytes max). Shown by
  /// most OS file managers and the iso9660 driver as the disc label. Default
  /// "CDROM". Truncated to 32 chars; upper-cased to fit the d-character set.
  /// </summary>
  public string VolumeIdentifier { get; set; } = "CDROM";

  /// <summary>
  /// ECMA-119 8.4.6 System Identifier (a-characters, 32 bytes max). Names the
  /// system that wrote the image. Empty by default.
  /// </summary>
  public string SystemIdentifier { get; set; } = "";

  /// <summary>
  /// ECMA-119 8.4.10 Publisher Identifier (a-characters, 128 bytes max).
  /// Empty by default.
  /// </summary>
  public string PublisherIdentifier { get; set; } = "";

  /// <summary>
  /// ECMA-119 8.4.12 Application Identifier (a-characters, 128 bytes max).
  /// Empty by default.
  /// </summary>
  public string ApplicationIdentifier { get; set; } = "";

  /// <summary>
  /// Adds a file to the image. The name may contain '/' path separators, in
  /// which case the intermediate segments are created as directories.
  /// </summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data, null, null));

  // ── Rock Ridge and El Torito ─────────────────────────────────────────────

  /// <summary>
  /// Adds Rock Ridge (RRIP 1.10 / IEEE P1282) to the primary tree: full POSIX names (NM),
  /// modes (PX) and modification times (TF), marked by SUSP "SP"/"ER" in the root. Off by
  /// default, so images stay byte-identical to before unless asked for.
  /// </summary>
  public bool EnableRockRidge { get; set; }

  private readonly Dictionary<string, (int? Mode, DateTime? Modified)> _meta = new(StringComparer.Ordinal);
  private readonly List<string> _explicitDirs = [];

  /// <summary>Adds a directory, also an empty one.</summary>
  public void AddDirectory(string name, int? mode = null, DateTime? modified = null) {
    var key = Key(name);
    if (key.Length == 0) return;
    _explicitDirs.Add(key);
    _meta[key] = (mode, modified);
  }

  /// <summary>Mode and modification time of a file or directory (for Rock Ridge and records).</summary>
  public void SetMetadata(string name, int? mode, DateTime? modified) => _meta[Key(name)] = (mode, modified);

  private static string Key(string name) => string.Join('/', name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

  /// <summary>An El Torito boot entry (the first one is the default entry).</summary>
  public sealed class BootEntry {
    /// <summary>0 = x86 BIOS, 0xEF = UEFI.</summary>
    public byte Platform;
    /// <summary>0 = no emulation.</summary>
    public byte MediaType;
    public ushort LoadSegment;
    public byte SystemType;
    /// <summary>Virtual 512-byte sectors loaded at boot.</summary>
    public ushort SectorCount;
    /// <summary>The boot image as a file of the image (its extent is where the entry points).</summary>
    public string? ImagePath;
    /// <summary>A boot image that is not a visible file.</summary>
    public byte[]? ImageData;
    /// <summary>Patch an isolinux-style boot info table (bytes 8–63) into the image.</summary>
    public bool PatchBootInfoTable;
  }

  /// <summary>El Torito entries; none means an image that does not boot.</summary>
  public List<BootEntry> BootEntries { get; } = [];

  /// <summary>A file of the image that is the boot catalog (e.g. "isolinux/boot.cat").</summary>
  public string? BootCatalogPath { get; set; }

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives extent + path-table
  /// + directory-record sizing in pass 1; bytes are pulled from
  /// <paramref name="openStream"/> in pass 2 of <see cref="BuildToStreaming"/>.
  /// Never buffered as <c>byte[]</c>.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  // ── Directory tree ───────────────────────────────────────────────────────

  private sealed class DirNode {
    public required string Name;            // ISO directory identifier (uppercase, made unique), "" for root
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

    public string RrName = "";              // Rock Ridge name (the real one)
    public string Path = "";
    public int? Mode;
    public DateTime? Modified;
    public ContinuationArea? Continuation;  // where an NM too long for the record went
  }

  private sealed class FileNode {
    public required string Identifier;      // ECMA-119 identifier, includes ";1" version suffix (made unique)
    public required string JolietName;      // original (long, mixed-case) file name (truncated per spec)
    public required byte[] Data;
    public long? StreamingSize;             // when set, data is streamed (Data is empty)
    public Func<Stream>? StreamOpener;      // streaming entries only
    public int Lba;
    public string RrName = "";
    public string Path = "";
    public int? Mode;
    public DateTime? Modified;
    public ContinuationArea? Continuation;
    /// <summary>Not written as data: its extent is the boot catalog.</summary>
    public bool IsBootCatalog;

    /// <summary>Logical file length, whether in-memory or streamed.</summary>
    public long Length => this.StreamingSize ?? this.Data.Length;
  }

  /// <summary>
  /// Builds the complete ISO 9660 image and returns it as a byte array.
  /// </summary>
  /// <summary>First sector of file data; everything below it is metadata.</summary>
  private int FileDataStartSector { get; set; }

  /// <summary>Declared size of the image the last build laid out.</summary>
  private long TotalImageBytes { get; set; }

  /// <summary>When set, Build materialises only the metadata prefix.</summary>
  private bool _prefixOnly;

  /// <summary>
  /// Performs the build operation.
  /// </summary>
  public byte[] Build() {
    var root = BuildTree();

    // Enumerate directories in breadth-first order: a node's parent always
    // precedes it, which is exactly the ordering the path table requires.
    var dirs = OrderDirectoriesBreadthFirst(root);

    // Compute each directory's extent size for both trees.
    foreach (var dir in dirs) {
      dir.Size = this.CalculateDirectorySize(dir, joliet: false);
      if (this.EnableJoliet)
        dir.JolietSize = this.CalculateDirectorySize(dir, joliet: true);
    }

    // Reserved sectors 0-15 (system area), then the volume-descriptor set:
    //   16 PVD, [17 Joliet SVD,] then the terminator.
    var joliet = this.EnableJoliet;
    var boot = this.BootEntries.Count > 0;
    var bootRecordLba = boot ? 17 : -1;
    var svdLba = joliet ? (boot ? 18 : 17) : -1;
    var terminatorLba = 17 + (boot ? 1 : 0) + (joliet ? 1 : 0);

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

    // The boot catalog gets a sector of its own unless it is a file of the image.
    var catalogFile = dirs.SelectMany(d => d.Files).FirstOrDefault(f => f.IsBootCatalog);
    var catalogLba = -1;
    if (boot && catalogFile == null) catalogLba = cursor++;

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

    // Rock Ridge continuation areas: names too long for their directory record.
    var continuations = new List<(ContinuationArea Area, byte[] Bytes)>();
    if (this.EnableRockRidge) {
      var areaOffset = 0;
      var areaLba = cursor;
      ContinuationArea? Place(string name, int idLen) {
        if (NameContinuation(name, idLen) is not { } bytes) return null;
        if (areaOffset + bytes.Length > SectorSize) {
          ++areaLba;
          areaOffset = 0;
        }
        var area = new ContinuationArea(areaLba, areaOffset, bytes.Length);
        continuations.Add((area, bytes));
        areaOffset += bytes.Length;
        return area;
      }
      foreach (var dir in dirs) {
        foreach (var child in dir.Children.Values)
          child.Continuation = Place(child.RrName, IdentifierLength(child.Name, isFile: false, joliet: false));
        foreach (var file in dir.Files)
          file.Continuation = Place(file.RrName, IdentifierLength(file.Identifier, isFile: true, joliet: false));
      }
      if (continuations.Count > 0) cursor = areaLba + 1;
    }

    // Shared file data after all directory extents; referenced by both trees.
    // Everything below this sector is metadata, so only that prefix has to be
    // materialised -- allocating the whole volume caps an ISO at the array limit,
    // which no DVD or Blu-ray image is obliged to respect.
    this.FileDataStartSector = cursor;
    foreach (var dir in dirs)
      foreach (var file in dir.Files) {
        file.Lba = cursor;
        var sectors = file.Length == 0 ? 1 : (int)((file.Length + SectorSize - 1) / SectorSize);
        if (file.IsBootCatalog) sectors = Math.Max(sectors, 1);
        cursor += sectors;
      }
    if (catalogFile != null) catalogLba = catalogFile.Lba;

    // Boot images: a file of the image, or hidden data after the files.
    var bootImages = new List<(BootEntry Entry, int Lba, byte[]? Data, FileNode? File)>();
    foreach (var e in this.BootEntries) {
      var file = e.ImagePath == null ? null : dirs.SelectMany(d => d.Files).FirstOrDefault(f => f.Path == Key(e.ImagePath));
      if (file != null) {
        bootImages.Add((e, file.Lba, null, file));
      } else if (e.ImageData != null) {
        bootImages.Add((e, cursor, e.ImageData, null));
        cursor += Math.Max(1, (e.ImageData.Length + SectorSize - 1) / SectorSize);
      }
    }

    // Append the conventional trailing post-gap so the recorded Volume Space Size
    // (and the backing file) clear the minimum size real readers such as cdrtools
    // isoinfo expect. The padding sectors are zero-filled slack at the tail.
    var totalSectors = cursor + TrailingPadSectors;
    this.TotalImageBytes = (long)totalSectors * SectorSize;
    var prefixBytes = this._prefixOnly
      ? (long)this.FileDataStartSector * SectorSize
      : this.TotalImageBytes;
    if (prefixBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"ISO: a {this.TotalImageBytes:N0}-byte image exceeds the array limit; write it to a seekable stream instead.");
    var image = new byte[prefixBytes];

    // Primary Volume Descriptor (sector 16).
    this.WriteVolumeDescriptor(image, 16, type: 1, totalSectors, root,
      pathTableSize, lPathLba, mPathLba, rootLba: root.Lba, rootSize: root.Size, joliet: false);

    // El Torito Boot Record (sector 17) and the boot catalog.
    if (boot) {
      var br = bootRecordLba * SectorSize;
      image[br] = 0;
      "CD001"u8.CopyTo(image.AsSpan(br + 1));
      image[br + 6] = 1;
      "EL TORITO SPECIFICATION"u8.CopyTo(image.AsSpan(br + 7));
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(br + 0x47), (uint)catalogLba);
      var catalog = BuildBootCatalog(bootImages.Select(b => (b.Entry, b.Lba)).ToList());
      if (catalogFile != null) {
        catalogFile.Data = catalog;
        catalogFile.StreamingSize = null;
        catalogFile.StreamOpener = null;
      } else if ((long)catalogLba * SectorSize + catalog.Length <= image.Length) {
        catalog.CopyTo(image, (long)catalogLba * SectorSize);
      }
      foreach (var (entry, lba, data, file) in bootImages) {
        if (file != null && entry.PatchBootInfoTable) {
          // A streamed boot image is small (a loader); it is read so the table can go in.
          if (file.StreamOpener is { } open) {
            using var source = open();
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            file.Data = copy.ToArray();
            file.StreamOpener = null;
            file.StreamingSize = null;
          }
          file.Data = PatchBootInfoTable(file.Data, lba);
        } else if (data != null) {
          var bytes = entry.PatchBootInfoTable ? PatchBootInfoTable(data, lba) : data;
          if (this._streamingSink != null) this._streamingSink.Add(((long)lba * SectorSize, bytes.Length, () => new MemoryStream(bytes, writable: false)));
          else bytes.CopyTo(image, (long)lba * SectorSize);
        }
      }
    }

    // Joliet Supplementary Volume Descriptor (sector 17), if enabled.
    if (joliet)
      this.WriteVolumeDescriptor(image, svdLba, type: 2, totalSectors, root,
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

    // Directory extents (primary then Joliet), then the Rock Ridge continuation areas.
    foreach (var dir in dirs)
      WriteDirectoryExtent(image, dir, joliet: false);
    if (joliet)
      foreach (var dir in dirs)
        WriteDirectoryExtent(image, dir, joliet: true);
    foreach (var (area, bytes) in continuations)
      bytes.CopyTo(image, (long)area.Lba * SectorSize + area.Offset);

    // Shared file data. Streaming entries leave their extent zero; the
    // streaming pass post-fills from this absolute byte offset. The sector
    // tail past the logical size stays zero (matches the in-memory path,
    // which copies only Data.Length bytes).
    foreach (var dir in dirs)
      foreach (var file in dir.Files) {
        if (file.StreamOpener != null) {
          if (this._streamingSink != null && file.Length > 0)
            this._streamingSink.Add(((long)file.Lba * SectorSize, file.Length, file.StreamOpener));
        } else if (this._streamingSink != null) {
          // Inline payloads are placed by seek too when building for a stream:
          // they sit past the materialised prefix.
          if (file.Length > 0) {
            var payload = file.Data;
            this._streamingSink.Add(((long)file.Lba * SectorSize, file.Length, () => new MemoryStream(payload, writable: false)));
          }
        } else {
          file.Data.CopyTo(image, (long)file.Lba * SectorSize);
        }
      }

    return image;
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 lays out the identical ISO image (same
  /// descriptors, path tables, directory extents, and file-data extent
  /// placement as <see cref="Build"/>) with each streaming file's data extent
  /// left zero; pass 2 seeks to each file's extent byte offset and copies its
  /// bytes from the opener in 64 KB chunks. The sector tail past each file's
  /// logical size stays zero, exactly as the in-memory path leaves it. For the
  /// same inputs (and same wall-clock second for the volatile ECMA-119
  /// timestamps) the output is byte-for-byte identical to <see cref="Build"/>.
  /// </summary>
  public void BuildToStreaming(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(long ByteOffset, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    this._prefixOnly = true;
    byte[] image;
    try {
      image = Build();
    } finally {
      this._streamingSink = null;
      this._prefixOnly = false;
    }

    output.SetLength(this.TotalImageBytes);
    output.Position = 0;
    output.Write(image);

    var buf = new byte[64 * 1024];
    foreach (var (byteOffset, size, opener) in sink) {
      if (size <= 0) continue;
      if (byteOffset < 0 || byteOffset >= output.Length) continue;
      output.Position = byteOffset;
      using var src = opener();
      long copied = 0;
      while (copied < size) {
        var want = (int)Math.Min(buf.Length, size - copied);
        var n = src.Read(buf, 0, want);
        if (n <= 0) break;
        output.Write(buf, 0, n);
        copied += n;
      }
      // Sector tail past `size` retains zero from the image init.
    }
    output.Flush();
  }

  private static int SectorsFor(int byteLength) {
    var sectors = (byteLength + SectorSize - 1) / SectorSize;
    return sectors < 1 ? 1 : sectors;
  }

  private DirNode BuildTree() {
    var root = new DirNode { Name = "" };
    if (_meta.TryGetValue("", out var rootMeta)) (root.Mode, root.Modified) = rootMeta;

    DirNode Descend(string[] segments, int count) {
      var dir = root;
      for (var i = 0; i < count; i++) {
        var key = this.EnableRockRidge ? segments[i] : NormalizeDirectoryName(segments[i]);
        if (!dir.Children.TryGetValue(key, out var child)) {
          var path = string.Join('/', segments.Take(i + 1));
          child = new DirNode {
            Name = NormalizeDirectoryName(segments[i]),
            JolietName = TruncateJolietName(segments[i]),
            RrName = segments[i],
            Path = path,
            Parent = dir,
          };
          if (_meta.TryGetValue(path, out var m)) (child.Mode, child.Modified) = m;
          dir.Children.Add(key, child);
        }
        dir = child;
      }
      return dir;
    }

    foreach (var d in _explicitDirs) {
      var segments = d.Split('/');
      Descend(segments, segments.Length);
    }

    foreach (var (rawName, data, streamingSize, opener) in _files) {
      var segments = rawName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;
      var dir = Descend(segments, segments.Length - 1);
      var path = string.Join('/', segments);
      var node = new FileNode {
        Identifier = NormalizeFileName(segments[^1]) + ";1",
        JolietName = TruncateJolietName(segments[^1]),
        RrName = segments[^1],
        Path = path,
        Data = data,
        StreamingSize = streamingSize,
        StreamOpener = opener,
        IsBootCatalog = this.BootCatalogPath != null && Key(this.BootCatalogPath) == path,
      };
      if (_meta.TryGetValue(path, out var m)) (node.Mode, node.Modified) = m;
      dir.Files.Add(node);
    }
    MakeIdentifiersUnique(root);
    return root;
  }

  /// <summary>
  /// Primary-tree identifiers must be unique within a directory; upper-casing (and, with
  /// Rock Ridge, sanitising) can make two names equal. Later ones get a "~N" suffix.
  /// </summary>
  private void MakeIdentifiersUnique(DirNode dir) {
    var taken = new HashSet<string>(StringComparer.Ordinal);
    foreach (var child in dir.Children.Values) {
      var name = child.Name;
      for (var n = 1; !taken.Add(name); n++) name = Suffix(child.Name, n);
      child.Name = name;
      MakeIdentifiersUnique(child);
    }
    foreach (var file in dir.Files) {
      var bare = file.Identifier[..^2];
      var id = file.Identifier;
      for (var n = 1; !taken.Add(id); n++) id = Suffix(bare, n) + ";1";
      file.Identifier = id;
    }

    static string Suffix(string name, int n) {
      var dot = name.LastIndexOf('.');
      return dot > 0 ? $"{name[..dot]}~{n}{name[dot..]}" : $"{name}~{n}";
    }
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

  private int CalculateDirectorySize(DirNode dir, bool joliet) {
    var rr = this.EnableRockRidge && !joliet;
    var parent = dir.Parent ?? dir;
    var size = 0;
    size = AppendRecord(size, 1, rr ? SelfSystemUse(dir).Length : 0);
    size = AppendRecord(size, 1, rr ? RockRidgeFields(parent.Mode, parent.Modified, isDir: true, null, 0, null).Length : 0);

    foreach (var child in dir.Children.Values) {
      var idLen = IdentifierLength(joliet ? child.JolietName : child.Name, isFile: false, joliet);
      size = AppendRecord(size, idLen, rr ? RockRidgeFields(child.Mode, child.Modified, isDir: true, child.RrName, idLen, null).Length : 0);
    }

    foreach (var file in dir.Files) {
      var idLen = IdentifierLength(joliet ? file.JolietName : file.Identifier, isFile: true, joliet);
      size = AppendRecord(size, idLen, rr ? RockRidgeFields(file.Mode, file.Modified, isDir: false, file.RrName, idLen, null).Length : 0);
    }

    if (size % SectorSize != 0)
      size += SectorSize - (size % SectorSize);
    if (size == 0) size = SectorSize;
    return size;
  }

  internal static int RecordLength(int idLen, int suLen) {
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;
    recLen += suLen;
    if ((recLen & 1) != 0) recLen++;
    return recLen;
  }

  private static int AppendRecord(int size, int idLen, int suLen = 0) {
    var recLen = RecordLength(idLen, suLen);
    var used = size % SectorSize;
    if (used + recLen > SectorSize)
      size += SectorSize - used; // a record may not span a sector boundary
    return size + recLen;
  }

  // ── Rock Ridge system use fields ───────────────────────────────────────

  /// <summary>Where a record's System Use continues (SUSP CE): sector, offset in it, length.</summary>
  internal sealed record ContinuationArea(int Lba, int Offset, int Length);

  private const int MaxRecordLength = 255;
  private const int PxLength = 36;   // RRIP 1.10 PX: mode, links, uid, gid (both-endian)
  private const int TfLength = 12;   // TF with the modification time in the 7-byte form
  private const int CeLength = 28;   // SUSP CE: block, offset, length (both-endian)
  private const int MaxNmPayload = 250;

  /// <summary>Whether an NM for <paramref name="name"/> fits the record next to PX and TF.</summary>
  internal static bool NameFitsRecord(string name, int idLen)
    => RecordLength(idLen, PxLength + TfLength + 5 + Encoding.UTF8.GetByteCount(name)) <= MaxRecordLength;

  /// <summary>
  /// The continuation area of a name too long for its record: the name as NM entries of at
  /// most 250 bytes, each but the last flagged CONTINUE; null when the name fits the record.
  /// </summary>
  internal static byte[]? NameContinuation(string name, int idLen) {
    if (NameFitsRecord(name, idLen)) return null;
    var bytes = Encoding.UTF8.GetBytes(name);
    var area = new List<byte>(bytes.Length + 16);
    for (var start = 0; start < bytes.Length;) {
      var take = Math.Min(MaxNmPayload, bytes.Length - start);
      area.AddRange("NM"u8.ToArray());
      area.Add((byte)(5 + take));
      area.Add(1);
      area.Add((byte)(start + take < bytes.Length ? 0x01 : 0x00)); // CONTINUE
      area.AddRange(bytes.AsSpan(start, take).ToArray());
      start += take;
    }
    return [.. area];
  }

  /// <summary>
  /// PX (mode, links, owner), TF (modification time) and, for named entries, NM. A name too
  /// long for the 255-byte record is written to a continuation area instead, which a CE entry
  /// in the record points to (<paramref name="continuation"/>; zeros while sizing).
  /// </summary>
  internal static byte[] RockRidgeFields(int? mode, DateTime? modified, bool isDir, string? name, int idLen, ContinuationArea? continuation) {
    var su = new List<byte>(96);
    var m = (uint)(mode ?? (isDir ? 0x1ED : 0x1A4)) & 0xFFF; // 0755 / 0644
    m |= isDir ? 0x4000u : 0x8000u;
    su.AddRange("PX"u8.ToArray());
    su.Add(36);
    su.Add(1);
    AddBoth(su, m);
    AddBoth(su, isDir ? 2u : 1u);
    AddBoth(su, 0);
    AddBoth(su, 0);
    var t = (modified ?? DateTime.UtcNow).ToUniversalTime();
    su.AddRange("TF"u8.ToArray());
    su.Add(12);
    su.Add(1);
    su.Add(0x02); // modification time, 7-byte form
    su.AddRange([(byte)(t.Year - 1900), (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second, 0]);
    if (name != null && NameFitsRecord(name, idLen)) {
      var bytes = Encoding.UTF8.GetBytes(name);
      su.AddRange("NM"u8.ToArray());
      su.Add((byte)(5 + bytes.Length));
      su.Add(1);
      su.Add(0);
      su.AddRange(bytes);
    } else if (name != null) {
      su.AddRange("CE"u8.ToArray());
      su.Add(CeLength);
      su.Add(1);
      AddBoth(su, (uint)(continuation?.Lba ?? 0));
      AddBoth(su, (uint)(continuation?.Offset ?? 0));
      AddBoth(su, (uint)(continuation?.Length ?? 0));
    }
    return su.ToArray();

    static void AddBoth(List<byte> l, uint v) {
      Span<byte> b = stackalloc byte[8];
      BinaryPrimitives.WriteUInt32LittleEndian(b, v);
      BinaryPrimitives.WriteUInt32BigEndian(b[4..], v);
      l.AddRange(b.ToArray());
    }
  }

  /// <summary>The "." record: in the root it also marks the image as using SUSP and RRIP.</summary>
  private static byte[] SelfSystemUse(DirNode dir) {
    var rr = RockRidgeFields(dir.Mode, dir.Modified, isDir: true, null, 1, null);
    if (dir.Parent != null) return rr;
    byte[] sp = [(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0];
    byte[] er = [(byte)'E', (byte)'R', 18, 1, 10, 0, 0, 1, .. "RRIP_1991A"u8];
    return [.. sp, .. er, .. rr];
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

  private void WriteVolumeDescriptor(
      byte[] image, int sectorLba, byte type, int totalSectors, DirNode root,
      int pathTableSize, int lPathLba, int mPathLba, int rootLba, int rootSize, bool joliet) {
    var off = sectorLba * SectorSize;
    image[off] = type; // 1 = PVD, 2 = SVD
    "CD001"u8.CopyTo(image.AsSpan(off + 1));
    image[off + 6] = 1; // Volume Descriptor Version

    // Offset 7: unused (PVD) / Volume Flags (SVD). Zero in both our cases.

    // ── String identifier fields ────────────────────────────────────────────
    // ECMA-119 8.4.x require every a-character / d-character identifier field to
    // be filled: with content where present, otherwise space-padded (0x20). The
    // Joliet SVD carries the same fields as UCS-2BE (a1/d1-characters), space
    // padded with the UCS-2BE space 0x0020. A leading 0x00 in these fields is
    // what makes libisofs reject the descriptor as "damaged".
    var sysId = this.SystemIdentifier ?? "";
    var volId = string.IsNullOrEmpty(this.VolumeIdentifier) ? "CDROM" : this.VolumeIdentifier;
    var pubId = this.PublisherIdentifier ?? "";
    var appId = this.ApplicationIdentifier ?? "";
    if (joliet) {
      PadUcs2(image, off + 8, 32, sysId);       // System Identifier (a1)
      PadUcs2(image, off + 40, 32, volId);      // Volume Identifier (d1)
      // Escape sequences (offset 88): UCS-2 level 3 -> 0x25 0x2F 0x45 ("%/E").
      image[off + 88] = 0x25;
      image[off + 89] = 0x2F;
      image[off + 90] = 0x45;
      PadUcs2(image, off + 190, 128, "");       // Volume Set Identifier (d1)
      PadUcs2(image, off + 318, 128, pubId);    // Publisher Identifier (a1)
      PadUcs2(image, off + 446, 128, "");       // Data Preparer Identifier (a1)
      PadUcs2(image, off + 574, 128, appId);    // Application Identifier (a1)
      PadUcs2(image, off + 702, 37, "");        // Copyright File Identifier (d1)
      PadUcs2(image, off + 739, 37, "");        // Abstract File Identifier (d1)
      PadUcs2(image, off + 776, 37, "");        // Bibliographic File Identifier (d1)
    } else {
      PadString(image, off + 8, 32, sysId);     // System Identifier (a)
      PadString(image, off + 40, 32, volId);    // Volume Identifier (d)
      // Offset 88..119 unused on the PVD; left zero per spec.
      PadString(image, off + 190, 128, "");     // Volume Set Identifier (d)
      PadString(image, off + 318, 128, pubId);  // Publisher Identifier (a)
      PadString(image, off + 446, 128, "");     // Data Preparer Identifier (a)
      PadString(image, off + 574, 128, appId);  // Application Identifier (a)
      PadString(image, off + 702, 37, "");      // Copyright File Identifier (d)
      PadString(image, off + 739, 37, "");      // Abstract File Identifier (d)
      PadString(image, off + 776, 37, "");      // Bibliographic File Identifier (d)
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

    // Both path-table locations must be set: little-endian L table (offset 140)
    // and big-endian M table (offset 148). The optional L/M tables (offsets 144
    // and 152) stay zero — there is no second copy.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 140), (uint)lPathLba);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 148), (uint)mPathLba);

    // Root directory record (offset 156). Single-byte 0x00 identifier.
    WriteDirectoryRecord(image, off + 156, rootLba, rootSize, 0x02, [0]);

    // ── Volume date-and-time fields (4 × 17 bytes, offsets 813..880) ─────────
    // ECMA-119 8.4.26-8.4.29: 16 ASCII digits (YYYYMMDDHHMMSShh) plus a signed
    // GMT-offset byte. Creation and modification carry "now"; expiration and
    // effective are "no date specified" = sixteen ASCII '0' digits + offset 0.
    var now = DateTime.UtcNow;
    WriteVolumeDateTime(image, off + 813, now);   // Creation
    WriteVolumeDateTime(image, off + 830, now);   // Modification
    WriteVolumeDateTime(image, off + 847, null);  // Expiration (none)
    WriteVolumeDateTime(image, off + 864, null);  // Effective (none)

    // File Structure Version (offset 881) — mandatory; libisofs requires 1.
    image[off + 881] = 1;
    // Offset 882 reserved (0x00); offsets 883..1394 "Application Used" and
    // 1395..2047 reserved are left zero per ECMA-119.
  }

  // ECMA-119 17-byte volume date-and-time: "YYYYMMDDHHMMSShh" (16 ASCII digits,
  // hundredths of a second) followed by one signed-byte GMT offset in 15-minute
  // intervals. A null value writes the "no date specified" form (all '0' digits,
  // zero offset) which the spec mandates instead of a zero-filled field.
  private static void WriteVolumeDateTime(byte[] image, int offset, DateTime? when) {
    if (when is null) {
      for (var i = 0; i < 16; i++) image[offset + i] = (byte)'0';
      image[offset + 16] = 0;
      return;
    }
    var t = when.Value;
    var s = $"{t.Year:D4}{t.Month:D2}{t.Day:D2}{t.Hour:D2}{t.Minute:D2}{t.Second:D2}{t.Millisecond / 10:D2}";
    Encoding.ASCII.GetBytes(s).CopyTo(image, offset);
    image[offset + 16] = 0; // GMT offset 0 (UTC)
  }

  // ── Directory record / extent writers ─────────────────────────────────────

  internal static void WriteDirectoryRecord(byte[] image, int off, int lba, int size, byte flags, byte[] identifier, byte[]? systemUse = null, DateTime? modified = null) {
    var idLen = identifier.Length;
    var suStart = 33 + idLen + ((idLen & 1) == 0 ? 1 : 0);
    var recLen = RecordLength(idLen, systemUse?.Length ?? 0);

    image[off] = (byte)recLen;
    systemUse?.CopyTo(image, off + suStart);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 2), (uint)lba);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 6), (uint)lba);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 10), (uint)size);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 14), (uint)size);

    var now = (modified ?? DateTime.UtcNow).ToUniversalTime();
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

  private void WriteDirectoryExtent(byte[] image, DirNode dir, bool joliet) {
    var rr = this.EnableRockRidge && !joliet;
    var lba = joliet ? dir.JolietLba : dir.Lba;
    var selfSize = joliet ? dir.JolietSize : dir.Size;
    var baseOff = lba * SectorSize;
    var pos = baseOff;

    // "." (self) and ".." (parent; root's parent is itself).
    var parent = dir.Parent ?? dir;
    var parentLba = joliet ? parent.JolietLba : parent.Lba;
    var parentSize = joliet ? parent.JolietSize : parent.Size;
    WriteDirectoryRecord(image, pos, lba, selfSize, 0x02, [0], rr ? SelfSystemUse(dir) : null, dir.Modified);
    pos += image[pos];
    WriteDirectoryRecord(image, pos, parentLba, parentSize, 0x02, [1], rr ? RockRidgeFields(parent.Mode, parent.Modified, true, null, 1, null) : null, parent.Modified);
    pos += image[pos];

    // Child directory records.
    foreach (var child in dir.Children.Values) {
      var identifier = DirectoryIdentifierBytes(child, joliet);
      var childLba = joliet ? child.JolietLba : child.Lba;
      var childSize = joliet ? child.JolietSize : child.Size;
      var su = rr ? RockRidgeFields(child.Mode, child.Modified, true, child.RrName, identifier.Length, child.Continuation) : null;
      pos = AdvancePastSectorBoundary(baseOff, pos, identifier.Length, su?.Length ?? 0);
      WriteDirectoryRecord(image, pos, childLba, childSize, 0x02, identifier, su, child.Modified);
      pos += image[pos];
    }

    // File records (both trees reference the same shared data extent).
    foreach (var file in dir.Files) {
      var identifier = FileIdentifierBytes(file, joliet);
      var su = rr ? RockRidgeFields(file.Mode, file.Modified, false, file.RrName, identifier.Length, file.Continuation) : null;
      pos = AdvancePastSectorBoundary(baseOff, pos, identifier.Length, su?.Length ?? 0);
      var length = file.IsBootCatalog ? SectorSize : (int)file.Length;
      WriteDirectoryRecord(image, pos, file.Lba, length, 0x00, identifier, su, file.Modified);
      pos += image[pos];
    }
  }

  private static byte[] DirectoryIdentifierBytes(DirNode dir, bool joliet) =>
    joliet ? Encoding.BigEndianUnicode.GetBytes(dir.JolietName)
           : Encoding.ASCII.GetBytes(dir.Name);

  private static byte[] FileIdentifierBytes(FileNode file, bool joliet) =>
    joliet ? Encoding.BigEndianUnicode.GetBytes(file.JolietName)
           : Encoding.ASCII.GetBytes(file.Identifier);

  private static int AdvancePastSectorBoundary(int baseOff, int pos, int idLen, int suLen = 0) {
    var recLen = RecordLength(idLen, suLen);
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

  private string NormalizeFileName(string name) => this.EnableRockRidge ? DChars(name, keepDot: true) : name.ToUpperInvariant();

  private string NormalizeDirectoryName(string name) => this.EnableRockRidge ? DChars(name, keepDot: false) : name.ToUpperInvariant();

  /// <summary>
  /// With Rock Ridge the primary names are only a fallback: they are made valid ISO 9660
  /// (level 2) identifiers — A–Z, 0–9, "_", one ".", at most 30 characters.
  /// </summary>
  internal static string DChars(string name, bool keepDot) {
    var upper = name.ToUpperInvariant();
    var dot = keepDot ? upper.LastIndexOf('.') : -1;
    string Clean(string x) => new(x.Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_').ToArray());
    var stem = Clean(dot > 0 ? upper[..dot] : upper);
    var ext = dot > 0 ? Clean(upper[(dot + 1)..]) : "";
    if (ext.Length > 8) ext = ext[..8];
    var max = 30 - (ext.Length > 0 ? ext.Length + 1 : 0);
    if (stem.Length > max) stem = stem[..max];
    if (stem.Length == 0) stem = "_";
    return ext.Length > 0 ? $"{stem}.{ext}" : stem;
  }

  // ── El Torito ──────────────────────────────────────────────────────────

  private static byte[] BuildBootCatalog(List<(BootEntry Entry, int Lba)> entries) {
    var cat = new byte[SectorSize];
    if (entries.Count == 0) return cat;
    // Validation entry: header 1, platform, checksum making the 16-bit word sum zero, 55 AA.
    cat[0] = 1;
    cat[1] = entries[0].Entry.Platform;
    cat[30] = 0x55;
    cat[31] = 0xAA;
    ushort sum = 0;
    for (var i = 0; i < 32; i += 2) sum += BinaryPrimitives.ReadUInt16LittleEndian(cat.AsSpan(i));
    BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(28), (ushort)(0x10000 - sum));
    WriteBootEntry(cat, 32, entries[0].Entry, entries[0].Lba);
    // Further entries: one section each (header 0x90, the last 0x91).
    var off = 64;
    for (var i = 1; i < entries.Count && off + 64 <= SectorSize; i++) {
      cat[off] = (byte)(i == entries.Count - 1 ? 0x91 : 0x90);
      cat[off + 1] = entries[i].Entry.Platform;
      BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(off + 2), 1);
      WriteBootEntry(cat, off + 32, entries[i].Entry, entries[i].Lba);
      off += 64;
    }
    return cat;
  }

  private static void WriteBootEntry(byte[] cat, int off, BootEntry e, int lba) {
    cat[off] = 0x88; // bootable
    cat[off + 1] = e.MediaType;
    BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(off + 2), e.LoadSegment);
    cat[off + 4] = e.SystemType;
    BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(off + 6), e.SectorCount);
    BinaryPrimitives.WriteUInt32LittleEndian(cat.AsSpan(off + 8), (uint)lba);
  }

  /// <summary>
  /// The isolinux boot info table: where the PVD and the image itself are, the image's
  /// length and a checksum of its bytes from offset 64 (bytes 8–63).
  /// </summary>
  private static byte[] PatchBootInfoTable(byte[] data, int lba) {
    if (data.Length < 64) return data;
    var d = (byte[])data.Clone();
    Array.Clear(d, 8, 56);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)lba);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)d.Length);
    uint sum = 0;
    for (var i = 64; i + 4 <= d.Length; i += 4) sum += BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i));
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), sum);
    return d;
  }

  // Joliet caps identifiers at 64 UCS-2 characters (128 bytes). Longer names are
  // truncated to the limit while preserving the extension where possible.
  internal static string TruncateJolietName(string name) {
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
