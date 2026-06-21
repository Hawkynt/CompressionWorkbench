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
/// </summary>
public sealed class MinixV1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable {
  public string Id => "MinixV1";
  public string DisplayName => "Minix V1 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
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
    using var w = new MinixV1Writer(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("MinixV1 defragmentation requires an in-place mover.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("MinixV1 read-only — defragmentation requires a writer.");
}
