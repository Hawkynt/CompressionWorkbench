#pragma warning disable CS1591
using Compression.Registry.Streaming;

namespace Compression.Registry;

/// <summary>
/// Auto-generated descriptor for compound tar formats (tar.gz, tar.bz2, etc.).
/// Wraps tar archive operations with a stream compression layer via the registry.
/// </summary>
public sealed class CompoundTarDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {

  /// <summary>
  /// Inherits the inner TAR descriptor's schema and adds a <c>CompressionLevel</c>
  /// knob for the wrapping stream compressor (gzip / bzip2 / xz / zstd / etc.).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema {
    get {
      var tarOps = FormatRegistry.GetArchiveOps("Tar");
      var baseSchema = (tarOps as IFormatOptionsSchema)?.OptionsSchema ?? [];
      var compressionKnob = new FormatOptionDescriptor(
        "CompressionLevel", "Compression level", FormatOptionKind.Integer, "5",
        AllowedValues: ["1", "5", "9"],
        Description: "Wrapping stream's compression level (inner format picks its own interpretation).");
      var result = new List<FormatOptionDescriptor>(baseSchema.Count + 1);
      result.AddRange(baseSchema);
      result.Add(compressionKnob);
      return result;
    }
  }

  private readonly string _id;
  private readonly string _displayName;
  private readonly string _streamFormatId;
  private readonly string _defaultExtension;
  private readonly IReadOnlyList<string> _compoundExtensions;

  public CompoundTarDescriptor(string id, string displayName, string streamFormatId,
      string defaultExtension, IReadOnlyList<string> compoundExtensions) {
    _id = id;
    _displayName = displayName;
    _streamFormatId = streamFormatId;
    _defaultExtension = defaultExtension;
    _compoundExtensions = compoundExtensions;
  }

  public string Id => _id;
  public string DisplayName => _displayName;
  public FormatCategory Category => FormatCategory.CompoundTar;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => _defaultExtension;
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => _compoundExtensions;
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tar", _displayName)];
  public string? TarCompressionFormatId => _streamFormatId;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var streamOps = FormatRegistry.GetStreamOps(_streamFormatId)!;
    var tarOps = FormatRegistry.GetArchiveOps("Tar")!;
    var wrapped = streamOps.WrapDecompress(stream);
    if (wrapped != null) {
      using (wrapped) return tarOps.List(wrapped, password);
    }
    using var ms = new MemoryStream();
    streamOps.Decompress(stream, ms);
    ms.Position = 0;
    return tarOps.List(ms, password);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var streamOps = FormatRegistry.GetStreamOps(_streamFormatId)!;
    var tarOps = FormatRegistry.GetArchiveOps("Tar")!;
    var wrapped = streamOps.WrapDecompress(stream);
    if (wrapped != null) {
      using (wrapped) tarOps.Extract(wrapped, outputDir, password, files);
      return;
    }
    using var ms = new MemoryStream();
    streamOps.Decompress(stream, ms);
    ms.Position = 0;
    tarOps.Extract(ms, outputDir, password, files);
  }

  /// <summary>
  /// Opens a single entry by first decompressing the outer stream (gzip /
  /// bzip2 / xz / zstd / etc.) and delegating to the inner TAR descriptor's
  /// own bounded <c>OpenEntry</c>. The decompressed TAR isn't seekable in
  /// general, so we materialise it into a <see cref="MemoryStream"/> once
  /// and let TAR's positional decoder produce the per-entry bounded view
  /// over that buffer. The returned stream is bounded to the inner entry's
  /// logical size — any block padding past the entry is unreachable.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var streamOps = FormatRegistry.GetStreamOps(_streamFormatId)!;
    var tarOps = FormatRegistry.GetArchiveOps("Tar")!;
    var ms = new MemoryStream();
    var wrapped = streamOps.WrapDecompress(archive);
    if (wrapped != null) {
      using (wrapped) wrapped.CopyTo(ms);
    } else {
      streamOps.Decompress(archive, ms);
    }
    ms.Position = 0;
    // TarFormatDescriptor.OpenEntry wraps in BoundedEntryStream sized to
    // the inner entry. The MemoryStream is owned by the returned stream
    // (leaveOpen=false in TAR's override) so disposal cascades correctly.
    return tarOps.OpenEntry(ms, entryName, password);
  }

  /// <summary>
  /// Native in-memory single-entry extraction routed through the bounded
  /// <see cref="OpenEntry"/> so the per-entry isolation contract is enforced
  /// uniformly across every compound tar variant.
  /// </summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var streamOps = FormatRegistry.GetStreamOps(_streamFormatId)!;
    var tarCreator = (IArchiveCreatable)FormatRegistry.GetArchiveOps("Tar")!;
    var wrapped = streamOps.WrapCompress(output);
    if (wrapped != null) {
      using (wrapped) tarCreator.Create(wrapped, inputs, options);
      return;
    }
    using var ms = new MemoryStream();
    tarCreator.Create(ms, inputs, options);
    ms.Position = 0;
    streamOps.Compress(ms, output);
  }
}
