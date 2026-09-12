#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Arc;

/// <summary>
/// ARC archive (System Enhancement Associates, 1985) — one of the first PC compression container formats.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/hyc/arc</c> — SEA ARC source (GPL continuation maintained by Howard Chu) — the reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ARC_(file_format)</c> — format history</description></item>
/// </list>
/// </summary>
public sealed class ArcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the ARC archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the ARC archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ArcReader(stream);
        var list = new List<(string Name, byte[] Data)>();
        while (r.GetNextEntry() is { } e)
          list.Add((e.FileName, r.ReadEntryData()));
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new ArcWriter(ms, ArcCompressionMethod.Crunched);
        foreach (var (n, d) in files) w.AddEntry(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new ArcReader(archive);
    while (r.GetNextEntry() is { } e) {
      var headerSize = e.Method >= ArcConstants.MethodStored ? ArcConstants.NewHeaderSize : ArcConstants.OldHeaderSize;
      // After GetNextEntry, stream is positioned at data start
      var dataStart = archive.Position;
      var headerStart = dataStart - headerSize;
      yield return new DefragBlockInfo(headerStart, headerSize, DefragBlockKind.MetadataReserved, FileName: $"Header: {e.FileName}");
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(dataStart, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
      // Skip past data to next entry
      archive.Position = dataStart + e.CompressedSize;
    }
    // End-of-archive marker (2 bytes: 0x1A 0x00)
    var eoaPos = archive.Position - 2; // GetNextEntry already consumed the 0x1A 0x00
    if (eoaPos >= 0)
      yield return new DefragBlockInfo(eoaPos, 2, DefragBlockKind.MetadataReserved, FileName: "End-of-archive");
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Arc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "ARC";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ARC archive.
  /// Uses <see cref="ArcModifier"/> — Add appends Stored before the EOA
  /// marker; Remove walks the entry chain and shifts trailing bytes
  /// (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ArcModifier.RemoveFile(archive, name, wipeData: true);
      ArcModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="ArcModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ArcModifier.RemoveFile(archive, name, wipeData: true);
  }
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".arc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".arc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1A], Confidence: 0.20)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("crunch", "Crunched"), new("store", "Store"), new("pack", "Packed"),
    new("squeeze", "Squeezed"), new("squash", "Squashed")
  ];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "ARC archive, one of the first PC compression formats";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ArcReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e)
      entries.Add(new(i++, e.FileName, e.OriginalSize, e.CompressedSize,
        $"Method {e.Method}", false, false, e.LastModified.DateTime));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ArcReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ReadEntryData());
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var arcMethod = options.MethodName switch {
      "store" => ArcCompressionMethod.Stored,
      "pack" or "packed" => ArcCompressionMethod.Packed,
      "squeeze" or "squeezed" => ArcCompressionMethod.Squeezed,
      "crunch5" => ArcCompressionMethod.Crunched5,
      "crunch6" => ArcCompressionMethod.Crunched6,
      "crunch7" => ArcCompressionMethod.Crunched7,
      "crunch" or "crunch8" => ArcCompressionMethod.Crunched,
      "squash" or "squashed" => ArcCompressionMethod.Squashed,
      _ => ArcCompressionMethod.Crunched,
    };
    var w = new ArcWriter(output, arcMethod);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }
}
