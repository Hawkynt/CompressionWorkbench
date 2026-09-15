#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.MessagePack;

/// <summary>MessagePack object graphs exposed as virtual folders and scalar files.</summary>
public sealed class MessagePackFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {
  public string Id => "MessagePack";
  public string DisplayName => "MessagePack object graph";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".msgpack";
  public IReadOnlyList<string> Extensions => [".msgpack", ".mpk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("messagepack", "MessagePack")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MessagePack maps become folders, arrays become indexed folders, scalar values and extension payloads become files.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, MessagePackCodec.Read);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, MessagePackCodec.Read);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, MessagePackCodec.Read);
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) => MessagePackCodec.Write(output, StructuredArchive.FromInputs(inputs));
}
