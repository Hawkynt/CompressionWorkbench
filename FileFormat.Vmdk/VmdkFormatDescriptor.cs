#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vmdk;

public sealed class VmdkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Vmdk";
  public string DisplayName => "VMDK";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate;
  public string DefaultExtension => ".vmdk";
  public IReadOnlyList<string> Extensions => [".vmdk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x4B, 0x44, 0x4D, 0x56], Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VMware virtual disk";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VmdkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new VmdkWriter();
    w.SetDiskData(fatImage);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VmdkReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
