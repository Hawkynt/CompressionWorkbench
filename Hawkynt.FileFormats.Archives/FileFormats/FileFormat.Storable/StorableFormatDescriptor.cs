#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Storable;

/// <summary>Portable/network-order Perl Storable data exposed without invoking thaw(), hooks, classes, or code references.</summary>
public sealed class StorableFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {
  public string Id => "Storable";
  public string DisplayName => "Perl Storable object graph";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".storable";
  public IReadOnlyList<string> Extensions => [".storable", ".sto"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("pst0"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("nstore", "Storable network order")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Safe subset of Perl Storable nstore/nfreeze: hashes, arrays, scalar bytes, integers, booleans, undef and references; executable/blessed/tied forms are rejected.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, StorableCodec.Read);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, StorableCodec.Read);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, StorableCodec.Read);
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) => StorableCodec.Write(output, StructuredArchive.FromInputs(inputs));
}
