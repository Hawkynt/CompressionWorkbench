using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Uc2;

/// <summary>UltraCompressor II archive (.uc2), Nico de Vries / AIP-NL, 1994-1996.</summary>
public sealed class Uc2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <inheritdoc />
  public string Id => "Uc2";
  /// <inheritdoc />
  public string DisplayName => "UltraCompressor II";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => ".uc2";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".uc2", ".pu2"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x55, 0x43, 0x32, 0x1A], Confidence: 0.95)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ultra", "Ultra (LZ77 + Huffman)")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description => "UltraCompressor II archive: LZ77+Huffman against shared master dictionaries, XOR-checksummed";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var a = Uc2Archive.Read(stream);
    return a.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsDirectory ? 0 : e.Size, e.CompressedSize,
      e.IsDirectory ? "" : e.Method == 80 ? "Turbo" : $"Ultra {e.Method}", e.IsDirectory, false, e.LastModified)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var a = Uc2Archive.Read(stream);
    foreach (var e in a.Entries) {
      if (e.IsDirectory || files is { Length: > 0 } && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, Uc2Archive.Extract(a, e));
    }
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    var a = Uc2Archive.Read(archive);
    var e = a.Entries.FirstOrDefault(x => !x.IsDirectory && string.Equals(x.Path, entryName, StringComparison.OrdinalIgnoreCase));
    return e is null ? [] : Uc2Archive.Extract(a, e);
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    if (!string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("UC2's UltraCrypt encryption is undocumented and is not written.");
    Uc2Archive.Write(output, inputs.Where(i => !i.IsDirectory).Select(i =>
      new Uc2Archive.Input(i.ArchiveName, i.ReadContent(), i.Metadata?.LastWriteTimeUtc?.LocalDateTime ?? DateTime.Now)));
  }
}
