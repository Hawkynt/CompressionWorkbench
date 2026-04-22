#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Msa;

public sealed class MsaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Msa";
  public string DisplayName => "MSA (Magic Shadow Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".msa";
  public IReadOnlyList<string> Extensions => [".msa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x0E, 0x0F], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rle", "RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Atari ST Magic Shadow Archiver disk image with RLE compression";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MsaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "RLE", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MsaReader(stream);
    foreach (var e in r.Entries)
      WriteFile(outputDir, e.Name, r.Extract(e));
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    if (fileList.Count == 0) return;
    var (_, data) = fileList[0]; // First file is the raw disk image
    MsaWriter.Write(output, data);
  }
}
