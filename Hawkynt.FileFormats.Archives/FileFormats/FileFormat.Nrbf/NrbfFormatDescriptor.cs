#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Nrbf;

/// <summary>MS-NRBF / legacy BinaryFormatter object graphs inspected without BinaryFormatter or type activation.</summary>
public sealed class NrbfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Nrbf";
  public string DisplayName => ".NET BinaryFormatter / NRBF object graph";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nrbf";
  public IReadOnlyList<string> Extensions => [".nrbf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ms-nrbf", "MS-NRBF")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Safe, non-instantiating reader for legacy .NET NRBF/BinaryFormatter streams; no assemblies are loaded and no constructors or callbacks execute.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, NrbfReader.Read);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, NrbfReader.Read);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, NrbfReader.Read);
}
