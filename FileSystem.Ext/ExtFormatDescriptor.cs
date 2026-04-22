#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ext;

public sealed class ExtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Ext";
  public string DisplayName => "ext2/3/4";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ext2";
  public IReadOnlyList<string> Extensions => [".ext2", ".ext3", ".ext4", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x53, 0xEF], 1080, 0.80f)]; // magic at superblock offset 1024 + field offset 56
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ext2/ext3/ext4 Linux filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ExtReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ExtWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ExtReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Add files to an existing ext2 image. Like the FAT variant, this rebuilds the
  /// image carrying over all existing files plus the new ones — <see cref="ExtWriter"/>
  /// is build-from-scratch, so "add" equals "re-pack".
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new ExtReader(archive);
    var combined = new ExtWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      combined.AddFile(name, data);
    var rebuilt = combined.Build();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Securely removes files from an existing ext2 image. Zeros the data blocks
  /// (including tip slack), the inode, the directory entry bytes, and updates
  /// bitmap and free-count accounting. No forensic recovery of the removed
  /// content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      ExtRemover.Remove(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }
}
