#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Auto-generated descriptor for compound tar formats (tar.gz, tar.bz2, etc.).
/// Wraps tar archive operations with a stream compression layer via the registry.
/// </summary>
public sealed class CompoundTarDescriptor : IFormatDescriptor, IArchiveFormatOperations {
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var streamOps = FormatRegistry.GetStreamOps(_streamFormatId)!;
    var tarOps = FormatRegistry.GetArchiveOps("Tar")!;
    var wrapped = streamOps.WrapCompress(output);
    if (wrapped != null) {
      using (wrapped) tarOps.Create(wrapped, inputs, options);
      return;
    }
    using var ms = new MemoryStream();
    tarOps.Create(ms, inputs, options);
    ms.Position = 0;
    streamOps.Compress(ms, output);
  }
}
