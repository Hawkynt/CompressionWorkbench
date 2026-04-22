#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Chm;

public sealed class ChmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Chm";
  public string DisplayName => "CHM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".chm";
  public IReadOnlyList<string> Extensions => [".chm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ITSF"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("chm", "CHM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Compiled HTML Help";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ChmReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.Size,
      e.Section == 0 ? "Stored" : "LZX", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ChmReader(stream);
    foreach (var e in r.Entries) {
      if (e.Size == 0) continue;
      if (e.Path.StartsWith("::")) continue; // skip internal entries
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      try {
        WriteFile(outputDir, e.Path.TrimStart('/'), r.Extract(e));
      } catch { /* skip entries that can't be decompressed */ }
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Default: section 0 (stored). Set MethodName="lzx" for LZX-compressed section 1.
    var w = new ChmWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    var useLzx = string.Equals(options.MethodName, "lzx", StringComparison.OrdinalIgnoreCase);
    w.WriteTo(output, useLzx);
  }
}
