#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Arc;

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

  public string Id => "Arc";
  public string DisplayName => "ARC";
  public FormatCategory Category => FormatCategory.Archive;
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
  public string DefaultExtension => ".arc";
  public IReadOnlyList<string> Extensions => [".arc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1A], Confidence: 0.20)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("crunch", "Crunched"), new("store", "Store"), new("pack", "Packed"),
    new("squeeze", "Squeezed"), new("squash", "Squashed")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ARC archive, one of the first PC compression formats";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ArcReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e)
      entries.Add(new(i++, e.FileName, e.OriginalSize, e.CompressedSize,
        $"Method {e.Method}", false, false, e.LastModified.DateTime));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ArcReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ReadEntryData());
    }
  }

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
