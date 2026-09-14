#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Reg;

/// <summary>Windows Registry export (.reg) files exposed as keys/folders and values/files.</summary>
public sealed class RegFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {
  public string Id => "Reg";
  public string DisplayName => "Windows Registry export";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".reg";
  public IReadOnlyList<string> Extensions => [".reg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("regedit5", "Registry Editor 5.00")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Offline .reg text files only: registry keys are folders and values are virtual files; the live Windows registry is never opened.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, RegCodec.Read);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, RegCodec.Read);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, RegCodec.Read);
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) => RegCodec.Write(output, StructuredArchive.FromInputs(inputs));
}
