#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vdi;

public sealed class VdiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Vdi";
  public string DisplayName => "VDI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".vdi";
  public IReadOnlyList<string> Extensions => [".vdi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x7F, 0x10, 0xDA, 0xBE], Offset: 64, Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("vdi", "VDI")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VirtualBox disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VdiReader(stream);
    return [new ArchiveEntryInfo(0, "disk.img", r.VirtualSize, stream.Length, "VDI", false, false, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VdiReader(stream);
    WriteFile(outputDir, "disk.img", r.ExtractDisk());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileFormat.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    using var w = new VdiWriter(output, leaveOpen: true, virtualSize: fatImage.Length);
    w.Write(fatImage);
  }
}
