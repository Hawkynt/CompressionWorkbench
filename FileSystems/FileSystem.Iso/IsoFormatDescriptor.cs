#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Iso;

/// <summary>
/// Format descriptor for ISO 9660 optical disc images.
/// </summary>
public sealed class IsoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {
  /// <inheritdoc/>
  public string Id => "Iso";
  /// <inheritdoc/>
  public string DisplayName => "ISO 9660";
  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc/>
  public string DefaultExtension => ".iso";
  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".iso"];
  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc/>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CD001"u8.ToArray(), Offset: 0x8001, Confidence: 0.95),
    new("CD001"u8.ToArray(), Offset: 0x8801, Confidence: 0.90),
    new("CD001"u8.ToArray(), Offset: 0x9001, Confidence: 0.85),
  ];
  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc/>
  public string Description => "ISO 9660 optical disc image";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new IsoReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  /// <inheritdoc/>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new IsoReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Adds or replaces files inside an existing ISO 9660 image. ISO 9660 has no
  /// free-space map and lays files out sequentially after the directory, so an
  /// incremental insert would require shifting downstream file data; the
  /// pragmatic approach is read-extract-rebuild. New entries are appended to
  /// the existing root directory; if a name collides, the new bytes win.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new IsoReader(archive);
    var newNames = new HashSet<string>(
      FilesOnly(inputs).Select(t => t.Name.ToUpperInvariant()),
      StringComparer.OrdinalIgnoreCase
    );
    var combined = new IsoWriter();
    // Carry forward every existing file that isn't being replaced.
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (newNames.Contains(entry.Name)) continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    foreach (var (name, data) in FilesOnly(inputs))
      combined.AddFile(name, data);
    var rebuilt = combined.Build();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ISO 9660 defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values supported;
  /// image is repacked with files reordered per mode.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new IsoReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new IsoWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>
  /// Removes the named entries from an existing ISO 9660 image. The whole image
  /// is rebuilt without the target entries — old file bytes are wiped because
  /// the new layout starts fresh, leaving no forensic trace of the removed
  /// content. Names match case-insensitively (ISO 9660 stores uppercase IDs).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    var reader = new IsoReader(archive);
    var toRemove = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    var combined = new IsoWriter();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (toRemove.Contains(entry.Name)) continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    var rebuilt = combined.Build();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
