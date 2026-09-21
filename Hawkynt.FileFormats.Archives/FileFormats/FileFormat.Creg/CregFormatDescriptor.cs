#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Creg;

/// <summary>Windows 95/98/Me binary CREG registry hives exposed as keys/folders and values/files.</summary>
public sealed class CregFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Creg";
  public string DisplayName => "Windows 9x Registry hive (CREG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dat";
  public IReadOnlyList<string> Extensions => [".dat", ".dao", ".pol"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("CREG"u8.ToArray(), Confidence: 0.99)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("creg", "Windows 9x CREG")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Offline Windows 95/98/Me CREG hives (SYSTEM.DAT, USER.DAT, CLASSES.DAT and related files); never opens the live registry.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, CregCodec.Read);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, CregCodec.Read);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, CregCodec.Read);
}
