#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.MacBinary;

/// <summary>
/// Describes mac binary format.
/// </summary>
public sealed class MacBinaryFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IArchiveCanonicalizable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "MacBinary";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "MacBinary";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Wrapper;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".bin";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bin", ".macbin"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("macbinary", "MacBinary", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Macintosh resource+data fork container encoding";

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) {
    var data = MacBinaryReader.ReadDataFork(input);
    output.Write(data);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    MacBinaryWriter.Write(output, "data", ms.ToArray());
  }

  /// <summary>
  /// Writes the canonical MacBinary form (<see cref="MacBinaryOptimizer"/>): reserved header
  /// fields and padding zeroed, version pair and header CRC repaired, bytes past the last
  /// declared section dropped. The data fork, resource fork, secondary header and Get Info
  /// comment are copied byte for byte.
  /// </summary>
  public void Canonicalize(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    MacBinaryOptimizer.Optimize(input, output);
    output.Flush();
    output.Position = 0;
  }
}
