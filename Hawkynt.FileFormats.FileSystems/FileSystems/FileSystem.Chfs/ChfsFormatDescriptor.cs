#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Chfs;

/// <summary>
/// CHFS — NetBSD's Chip File System for raw NOR and NAND flash. An image starts with the
/// erase-counter header magic "RDES" (<c>0x53454452</c>).
/// </summary>
/// <remarks>
/// Proof: NetBSD 9.5's kernel mounts the writer's images on <c>nandemulator(4)</c> (NAND, 2 KiB
/// pages, 128 KiB eraseblocks) and lists every entry with the mode, owner, mtime and content it
/// was written with. It also lists a <c>makefs</c> image after this package's add and remove,
/// and what the kernel writes there reads back through <see cref="ChfsReader"/>. Images NetBSD
/// 11.1's <c>makefs -t chfs</c> built, NAND and NOR, read back byte-identical to their source
/// trees, and given page-sized data nodes the writer reproduces both byte for byte apart from
/// data-node versions (see <c>docs/CHFS-ON-DISK.md</c>). Other geometries rest on that
/// comparison alone.
/// Logs that remove names (written only by a mounted file system) are refused.
/// Sourcing: rung 1 — converted from NetBSD's BSD-licensed CHFS headers and <c>makefs</c> CHFS back end.
/// </remarks>
public sealed class ChfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Chfs";
  public string DisplayName => "CHFS (NetBSD Chip File System)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".chfs";
  public IReadOnlyList<string> Extensions => [".chfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("RDES"u8.ToArray(), Offset: 0, Confidence: 0.55)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NetBSD CHFS flash image (NOR or NAND eraseblocks); written as makefs -t chfs lays it out.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ChfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsFile || e.IsSymlink ? e.Data.Length : 0,
      e.IsFile || e.IsSymlink ? e.Data.Length : 0, "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime),
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(e.Data) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ChfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
      else if (e.IsFile) WriteFile(outputDir, e.Path, e.Data);
    }
  }

  /// <summary>
  /// Writes a NAND image with <c>makefs</c>'s default geometry (2 KiB pages, 128 KiB eraseblocks,
  /// the geometry of NetBSD's <c>nandemulator(4)</c>): directories 0755 and files 0644 unless the
  /// input carries a Unix mode, owner and group from the input (root otherwise), and each input's
  /// modification time as its access, modification and change time.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var root = new Folder("", null);
    Place(root, inputs, replace: false);
    ChfsWriter.Write(output, root.Node());
  }

  /// <summary>
  /// Adds or replaces files by laying the image out again with the same geometry. Everything the
  /// image holds is kept: names, contents, modes, owners, the three times, link targets, device
  /// numbers, directory sizes and each eraseblock's erase count.
  /// </summary>
  /// <exception cref="NotSupportedException">The image holds hard links, which a relayout would split.</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var (reader, root) = Load(archive);
    Place(root, inputs, replace: true);
    Save(archive, reader, root);
  }

  /// <summary>Removes files or whole directories by laying the image out again (see <see cref="Add(Stream, IReadOnlyList{ArchiveInputInfo})"/>).</summary>
  /// <exception cref="FileNotFoundException">A name is not in the image; the image is left unchanged.</exception>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var (reader, root) = Load(archive);
    var present = reader.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
    var names = entryNames.Select(n => string.Join('/', n.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))).ToList();
    for (var i = 0; i < names.Count; ++i)
      if (!present.Contains(names[i]))
        throw new FileNotFoundException($"CHFS image has no entry '{entryNames[i]}'.", entryNames[i]);
    // a name inside a directory removed earlier in the same call is already gone
    foreach (var parts in names.Select(n => n.Split('/')))
      root.Find(parts[..^1])?.Remove(parts[^1]);
    Save(archive, reader, root);
  }

  private static (ChfsReader Reader, Folder Root) Load(Stream archive) {
    var reader = new ChfsReader(archive);
    if (reader.Entries.Where(e => !e.IsDirectory).GroupBy(e => e.Vno).Any(g => g.Count() > 1))
      throw new NotSupportedException("CHFS image holds hard links; laying it out again would split them.");
    var root = new Folder("", null);
    foreach (var e in reader.Entries) {
      var parts = e.Path.Split('/');
      var parent = root.Find(parts[..^1]) ?? throw new InvalidDataException($"CHFS entry '{e.Path}' has no parent.");
      var node = new ChfsWriteNode(parts[^1], e.Mode) {
        Uid = e.Uid, Gid = e.Gid, ATime = e.ATime, MTime = e.MTime, CTime = e.CTime,
        DirectorySize = e.IsDirectory ? e.Size : ChfsWriter.DefaultDirectorySize, Data = e.Data,
      };
      if (e.IsDirectory) parent.Folders.Add(new Folder(node.Name, node));
      else parent.Files.Add(node);
    }
    return (reader, root);
  }

  private static void Save(Stream archive, ChfsReader reader, Folder root) {
    using var image = new MemoryStream();
    ChfsWriter.Write(image, root.Node(), new ChfsGeometry(reader.PageSize, reader.EraseBlockSize, reader.IsNand), reader.EraseCounts);
    archive.Position = 0;
    archive.SetLength(0);
    image.Position = 0;
    image.CopyTo(archive);
    archive.Flush();
  }

  private static void Place(Folder root, IReadOnlyList<ArchiveInputInfo> inputs, bool replace) {
    foreach (var input in inputs) {
      var parts = input.ArchiveName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var meta = input.Metadata;
      var time = Seconds(input);
      var folder = root;
      for (var i = 0; i < parts.Length - 1; ++i)
        folder = folder.Sub(parts[i], null, time);
      if (input.IsDirectory) {
        folder.Sub(parts[^1], Attributes(new ChfsWriteNode(parts[^1], 0x4000 | Permissions(meta, 0x1ED)), meta, time), time);
        continue;
      }
      if (folder.Folders.Exists(f => f.Name == parts[^1]))
        throw new ArgumentException($"'{input.ArchiveName}' is a directory in the image.", nameof(inputs));
      var existing = folder.Files.FindIndex(f => f.Name == parts[^1]);
      if (existing >= 0 && !replace)
        throw new ArgumentException($"'{input.ArchiveName}' is added twice.", nameof(inputs));
      var file = Attributes(new ChfsWriteNode(parts[^1], 0x8000 | Permissions(meta, 0x1A4)) { Data = input.ReadContent() }, meta, time);
      if (existing >= 0) folder.Files[existing] = file;
      else folder.Files.Add(file);
    }
  }

  private static ChfsWriteNode Attributes(ChfsWriteNode node, ArchiveEntryMetadata? meta, uint time) => node with {
    Uid = meta?.UnixUserId ?? 0, Gid = meta?.UnixGroupId ?? 0,
    ATime = Seconds(meta?.LastAccessTimeUtc) ?? time, MTime = time, CTime = Seconds(meta?.StatusChangeTimeUtc) ?? time,
  };

  private static uint Permissions(ArchiveEntryMetadata? meta, uint fallback) => (meta?.UnixMode ?? fallback) & 0xFFFu;

  private static uint Seconds(ArchiveInputInfo input) {
    var time = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
      ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : DateTime.UtcNow);
    return Seconds(new DateTimeOffset(time, TimeSpan.Zero))!.Value;
  }

  private static uint? Seconds(DateTimeOffset? time) {
    if (time is not { } t) return null;
    var seconds = t.ToUnixTimeSeconds();
    return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  /// <summary>A directory being assembled; <see cref="Self"/> is null only for the root.</summary>
  private sealed class Folder(string name, ChfsWriteNode? self) {
    public string Name { get; } = name;
    public ChfsWriteNode? Self { get; set; } = self;
    public List<Folder> Folders { get; } = [];
    public List<ChfsWriteNode> Files { get; } = [];

    public Folder Sub(string name, ChfsWriteNode? self, uint time) {
      var folder = this.Folders.Find(f => f.Name == name);
      if (folder != null) {
        if (self != null) folder.Self = self;
        return folder;
      }
      if (this.Files.Exists(f => f.Name == name))
        throw new ArgumentException($"'{name}' is a file in the image, not a directory.");
      folder = new Folder(name, self ?? new ChfsWriteNode(name, 0x41ED) { ATime = time, MTime = time, CTime = time });
      this.Folders.Add(folder);
      return folder;
    }

    public Folder? Find(ReadOnlySpan<string> parts) {
      var folder = this;
      foreach (var part in parts) {
        folder = folder.Folders.Find(f => f.Name == part);
        if (folder == null) return null;
      }
      return folder;
    }

    public bool Remove(string name) => this.Files.RemoveAll(f => f.Name == name) + this.Folders.RemoveAll(f => f.Name == name) > 0;

    public ChfsWriteNode Node()
      => (this.Self ?? new ChfsWriteNode(this.Name, 0x41ED)) with { Name = this.Name, Children = [.. this.Folders.Select(f => f.Node()), .. this.Files] };
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var name = entryName.Replace('\\', '/').TrimStart('/');
    return (new ChfsReader(archive).Entries.FirstOrDefault(e => e.IsFile && e.Path == name)
      ?? throw new FileNotFoundException($"CHFS image has no file '{entryName}'.", entryName)).Data;
  }
}
