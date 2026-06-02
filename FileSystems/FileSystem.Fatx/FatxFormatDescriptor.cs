#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fatx;

/// <summary>
/// R/W descriptor for Microsoft Xbox / Xbox 360 FATX volumes.
/// Magic "FATX" at offset 0; 4 KiB superblock followed by FAT16/FAT32 table.
/// Read via <see cref="FatxReader"/>, create via <see cref="FatxWriter"/>,
/// mutate via <see cref="FatxModifier"/> (in-place Add/Remove on the root
/// directory; sub-directory mutation stays out of scope).
/// </summary>
public sealed class FatxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Fatx";
  public string DisplayName => "FATX (Xbox)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".fatx";
  public IReadOnlyList<string> Extensions => [".fatx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'F', (byte)'A', (byte)'T', (byte)'X'], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Xbox/Xbox 360 FATX filesystem image (R/W: list/extract/create/add/remove at root; FAT16+FAT32 width-aware).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FatxReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FatxReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FatxReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Emits a fresh FATX volume containing <paramref name="inputs"/> via
  /// <see cref="FatxWriter"/>. Path components in <c>ArchiveName</c> become
  /// nested FATX subdirectories (one cluster chain per directory); files
  /// are stored contiguously starting at the next free cluster.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new FatxWriter();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    var image = w.Build();
    output.Write(image);
  }

  /// <summary>
  /// In-place add: each input becomes a new dirent in the root cluster of the
  /// existing FATX image, with its bytes written into the first contiguous
  /// free cluster run found in the FAT. Sub-directory adds are not supported
  /// by v1 — only leaf filenames go to root. The FAT16/FAT32 width is
  /// auto-detected from the on-disk geometry.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      FatxModifier.AddFile(image, input.ArchiveName, input.ReadContent());
    }
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// In-place remove: tombstones each named dirent (name_length = 0xE5) and
  /// frees + wipes every data cluster in the file's FAT chain. Unknown names
  /// are silently skipped (consistent with how WORM Extract treats them).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      FatxModifier.RemoveFile(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }
}
