#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MinixV1;

/// <summary>
/// Read-only descriptor for the original Minix v1 filesystem (1987,
/// Tanenbaum). 1024-byte blocks, 16-bit zone numbers, 32-byte inodes
/// (7 direct + 1 indirect + 1 double-indirect), magic 0x137F (14-byte
/// names) or 0x138F (30-byte names — Coherent variant). Predecessor to
/// Linux's ext filesystem family.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/blob/master/include/uapi/linux/minix_fs.h</c> — canonical on-disk structures (v1 layout + 0x137F/0x138F magics)</description></item>
///   <item><description>Tanenbaum &amp; Woodhull, "Operating Systems: Design and Implementation" — the original Minix FS design</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Minix_file_system</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class MinixV1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {
  /// <summary>
  /// Minix v1 geometry (1024-byte blocks, 32-byte inodes) is fixed, but the
  /// on-disk directory-name width is a genuine format variant the writer
  /// honours: 14-byte names (magic 0x137F) or 30-byte names (magic 0x138F).
  /// Selecting "30" changes both the superblock magic and every directory
  /// entry's size.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "NameLength", DisplayName: "Directory Name Length", Kind: FormatOptionKind.Enum, Default: "14",
      AllowedValues: ["14", "30"],
      Description: "Directory-entry name width: 14 bytes (magic 0x137F) or 30 bytes (magic 0x138F)."),
  ];

  public string Id => "MinixV1";
  public string DisplayName => "Minix V1 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".minix1";
  public IReadOnlyList<string> Extensions => [".minix1"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // V1 magic at superblock+16 == file offset 1040
    new([0x7F, 0x13], Offset: 1040, Confidence: 0.85),  // 0x137F: 14-char names
    new([0x8F, 0x13], Offset: 1040, Confidence: 0.85),  // 0x138F: 30-char names
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minix v1 filesystem image (1987) — read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixV1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixV1Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single file entry as a bounded stream over the inode's reassembled
  /// data zones. Reads past the entry's logical size return 0 (EOF).
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new MinixV1Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Creates a fresh Minix v1 image holding the supplied inputs. Path
  /// separators in an input's archive name produce nested directory inodes,
  /// each with its own <c>"."</c>/<c>".."</c> entries.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var longNames = options.GetOptionInt("NameLength", 14) == 30;
    using var w = new MinixV1Writer(output, leaveOpen: true, longNames: longNames);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Minix v1 image via
  /// <see cref="MinixV1InPlaceModifier"/> — TRUE in-place O(touched bytes) I/O
  /// (allocate inode + data zones, append zones at EOF when the image is full,
  /// write the directory entry). Falls back to a whole-image rebuild only for
  /// nested paths or payloads beyond the direct + single-indirect ceiling.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        MinixV1InPlaceModifier.RemoveFile(archive, name, wipeData: true);
        MinixV1InPlaceModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new MinixV1Reader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage);
    }
  }

  /// <summary>Removes the named entries in-place via <see cref="MinixV1InPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var leftover = new List<string>();
    foreach (var name in entryNames) {
      var leaf = name.Replace('\\', '/').TrimStart('/');
      if (leaf.Contains('/') || !MinixV1InPlaceModifier.RemoveFile(archive, leaf, wipeData: true))
        leftover.Add(name);
    }
    if (leftover.Count == 0) return;
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, leftover.ToArray(),
      readEntries: stream => {
        var r = new MinixV1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);
  }

  private byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var w = new MinixV1Writer(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("MinixV1 defragmentation requires an in-place mover.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("MinixV1 read-only — defragmentation requires a writer.");
}
