#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.U8;

public sealed class U8FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "U8";
  public string DisplayName => "Nintendo U8";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".u8";
  public IReadOnlyList<string> Extensions => [".u8", ".arc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0x55, 0xAA, 0x38, 0x2D }, Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("u8", "U8")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo U8 archive (Wii / Wii U / 3DS / Switch)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new U8Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new U8Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory)
        continue;
      if (files != null && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new U8Writer(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var data = File.ReadAllBytes(input.FullPath);
      w.AddEntry(input.ArchiveName, data);
    }
  }
}
