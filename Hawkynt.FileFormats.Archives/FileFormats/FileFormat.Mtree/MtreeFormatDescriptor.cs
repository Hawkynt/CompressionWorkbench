using Compression.Registry;
using Compression.Registry.Streaming;

namespace FileFormat.Mtree;

/// <summary>
/// BSD mtree(5) filesystem manifest.
/// </summary>
/// <remarks>
/// mtree describes filesystem objects and metadata; it does not embed file bodies.
/// Consequently this descriptor intentionally advertises listing/creation but not
/// extraction. Dereferencing arbitrary <c>contents=</c> host paths from an untrusted
/// manifest would violate CWB's self-contained archive and path-isolation guarantees.
/// </remarks>
public sealed class MtreeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <inheritdoc />
  public string Id => "Mtree";

  /// <inheritdoc />
  public string DisplayName => "mtree";

  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <inheritdoc />
  public string DefaultExtension => ".mtree";

  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".mtree"];

  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'#', (byte)'m', (byte)'t', (byte)'r', (byte)'e', (byte)'e'], Confidence: 0.98),
  ];

  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mtree", "BSD mtree manifest")];

  /// <inheritdoc />
  public string? TarCompressionFormatId => null;

  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc />
  public string Description => "BSD mtree filesystem hierarchy manifest (metadata only; no embedded file bodies)";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new MtreeReader(stream).ReadAll();
    var result = new List<ArchiveEntryInfo>(entries.Count);
    for (var i = 0; i < entries.Count; ++i) {
      var entry = entries[i];
      result.Add(new ArchiveEntryInfo(
        i,
        entry.Path,
        entry.Size ?? 0,
        -1,
        "mtree",
        entry.IsDirectory,
        false,
        entry.ModificationTime?.UtcDateTime,
        Kind: "manifest",
        IsSymlink: entry.IsSymlink,
        LinkTarget: entry.LinkTarget));
    }
    return result;
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => throw new NotSupportedException(
      "mtree is a metadata manifest and contains no file bodies. CWB intentionally does not dereference manifest contents= paths on the host filesystem.");

  /// <inheritdoc />
  public Stream OpenEntry(Stream archive, string entryName, string? password)
    => throw new NotSupportedException(
      "mtree entries describe external filesystem objects; the manifest itself has no embedded entry stream.");

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    using var writer = new MtreeWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      var size = input.IsDirectory
        ? null
        : input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      writer.WriteEntry(new MtreeEntry {
        Path = input.ArchiveName,
        Type = input.IsDirectory ? MtreeEntryType.Directory : MtreeEntryType.File,
        Mode = input.IsDirectory ? 0x1EDu : 0x1A4u,
        Size = size,
      });
    }
    writer.Flush();
  }

  /// <inheritdoc />
  public void CreateFromStreams(
    Stream target,
    IEnumerable<StreamingArchiveInput> inputs,
    FormatCreateOptions options
  ) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);

    using var writer = new MtreeWriter(target, leaveOpen: true);
    foreach (var input in inputs) {
      writer.WriteEntry(new MtreeEntry {
        Path = input.Name,
        Type = input.IsDirectory ? MtreeEntryType.Directory : MtreeEntryType.File,
        Mode = input.IsDirectory ? 0x1EDu : 0x1A4u,
        Size = input.IsDirectory ? null : input.Size,
      });
    }
    writer.Flush();
  }
}
