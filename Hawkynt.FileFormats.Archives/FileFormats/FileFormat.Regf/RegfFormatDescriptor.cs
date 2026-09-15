#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Regf;

/// <summary>Windows NT through Windows 11 binary REGF registry hives exposed as keys/folders and values/files.</summary>
public sealed class RegfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Regf";
  public string DisplayName => "Windows Registry hive (REGF)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".hiv";
  public IReadOnlyList<string> Extensions => [".hiv", ".hive", ".hve"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("regf"u8.ToArray(), Confidence: 0.99)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("regf-standard", "REGF standard hive"),
    new("regf-latest", "REGF latest hive"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Offline Windows NT-family REGF hives, including standard and latest layouts used from NT through Windows 11; never opens the live registry.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, RegfCodec.Read);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, RegfCodec.Read);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, RegfCodec.Read);
}
