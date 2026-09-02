#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ktx2;

/// <summary>
/// Read-only pseudo-archive descriptor for Khronos KTX2 texture containers.
/// Lists the file as FULL + parsed header metadata + per-mip-level raw blobs +
/// key/value metadata, without transcoding any supercompressed (Basis/Zstd/ZLIB)
/// level data.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://registry.khronos.org/KTX/</c> — Khronos KTX registry — hosts the KTX 2.0 specification</description></item>
///   <item><description><c>https://github.com/KhronosGroup/KTX-Software</c> — reference implementation (libktx and tools)</description></item>
/// </list>
/// </summary>
public sealed class Ktx2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ktx2";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "KTX2 Texture";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ktx2";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ktx2"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A], Confidence: 0.99),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Khronos KTX2 texture container surfaced as a read-only pseudo-archive " +
    "(FULL + header metadata + per-mip-level raw blobs + key/value data); " +
    "supercompressed level data is exposed verbatim, never transcoded.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var file = ReadAll(stream);
    var entries = Ktx2Decomposer.Decompose(file);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var file = ReadAll(stream);
    foreach (var e in Ktx2Decomposer.Decompose(file)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
