#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Bsa;

public sealed class BsaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Bsa";
  public string DisplayName => "BSA";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bsa";
  public IReadOnlyList<string> Extensions => [".bsa", ".ba2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x01, 0x00, 0x00], Confidence: 0.40),
    new([(byte)'B', (byte)'S', (byte)'A', 0x00], Confidence: 0.90),
    new([(byte)'B', (byte)'T', (byte)'D', (byte)'X'], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bsa", "BSA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Bethesda Softworks Archive (Elder Scrolls/Fallout)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BsaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.OriginalSize,
      e.CompressedSize < 0 ? -1 : e.CompressedSize,
      e.IsCompressed ? "zlib" : "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BsaReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new BsaWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }
}
