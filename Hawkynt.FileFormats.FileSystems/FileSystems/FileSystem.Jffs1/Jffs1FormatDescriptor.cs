#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jffs1;

/// <summary>
/// JFFS version 1 — Axis Communications' Journalling Flash File System (Linux 2.4, removed
/// in 2.6). Magic "1984" in either byte order at offset 0, followed by the root inode 1.
/// </summary>
/// <remarks>
/// Proof: images <c>mkfs.jffs</c> (mtd-utils, as kept in uClinux/DSLinux) builds in both byte
/// orders read back byte-identical to the tree it was given, with modes and link targets; given
/// that tree in the order <c>mkfs.jffs</c> met it, <see cref="Jffs1Writer"/> reproduces both
/// images byte for byte. Logs of a running file system — deleted, renamed or overwritten nodes —
/// are refused: the kernel driver that defined their replay is gone, so a replay could not be
/// checked. Sourcing: rung 2 — the GPL tool was read for <c>docs/JFFS1-ON-DISK.md</c> and used
/// as a black-box oracle.
/// </remarks>
public sealed class Jffs1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Jffs1";
  public string DisplayName => "JFFS (version 1)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".jffs";
  public IReadOnlyList<string> Extensions => [".jffs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x31, 0x39, 0x38, 0x34, 0x01, 0x00, 0x00, 0x00], Offset: 0, Confidence: 0.60),
    new([0x34, 0x38, 0x39, 0x31, 0x00, 0x00, 0x00, 0x01], Offset: 0, Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "JFFS v1 flash image (mkfs.jffs layout, either byte order); written as mkfs.jffs writes it.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Jffs1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsFile || e.IsSymlink ? e.Data.Length : 0,
      e.IsFile || e.IsSymlink ? e.Data.Length : 0, "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime),
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(e.Data) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Jffs1Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
      else if (e.IsFile) WriteFile(outputDir, e.Path, e.Data);
    }
  }

  /// <summary>
  /// Writes a little-endian image the way <c>mkfs.jffs</c> does: directories 0755, files 0644, owner
  /// root, each input's modification time as its access, modification and change time.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var root = new Folder();
    foreach (var input in inputs) {
      var parts = input.ArchiveName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var folder = root;
      for (var i = 0; i < parts.Length - 1; ++i)
        folder = folder.Sub(parts[i]);
      var time = Seconds(input);
      if (input.IsDirectory) {
        folder.Sub(parts[^1]).Time ??= time;
        continue;
      }
      if (folder.Files.Exists(f => f.Name == parts[^1]))
        throw new ArgumentException($"'{input.ArchiveName}' is added twice.", nameof(inputs));
      folder.Files.Add(new(parts[^1], 0x81A4) { Data = input.ReadContent(), ATime = time, MTime = time, CTime = time });
    }
    Jffs1Writer.Write(output, root.Node(""));
  }

  private static uint Seconds(ArchiveInputInfo input) {
    var time = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
      ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : DateTime.UtcNow);
    var seconds = (time - DateTime.UnixEpoch).TotalSeconds;
    return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  private sealed class Folder {
    public uint? Time { get; set; }
    public List<(string Name, Folder Folder)> Folders { get; } = [];
    public List<Jffs1WriteNode> Files { get; } = [];

    public Folder Sub(string name) {
      foreach (var (n, f) in this.Folders)
        if (n == name) return f;
      var folder = new Folder();
      this.Folders.Add((name, folder));
      return folder;
    }

    public Jffs1WriteNode Node(string name) {
      var time = this.Time ?? 0;
      return new(name, 0x41ED) {
        ATime = time, MTime = time, CTime = time,
        Children = [.. this.Folders.Select(f => f.Folder.Node(f.Name)), .. this.Files],
      };
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new Jffs1Reader(archive);
    var name = entryName.Replace('\\', '/').TrimStart('/');
    return (r.Entries.FirstOrDefault(e => e.IsFile && e.Path == name)
      ?? throw new FileNotFoundException($"JFFS image has no file '{entryName}'.", entryName)).Data;
  }
}
