#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Arc;

public sealed class ArcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
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
