#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ktx2;

/// <summary>
/// Read-only pseudo-archive descriptor for Khronos KTX2 texture containers.
/// Lists the file as FULL + parsed header metadata + per-mip-level raw blobs +
/// key/value metadata, without transcoding any supercompressed (Basis/Zstd/ZLIB)
/// level data.
/// </summary>
public sealed class Ktx2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Ktx2";
  public string DisplayName => "KTX2 Texture";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ktx2";
  public IReadOnlyList<string> Extensions => [".ktx2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A], Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Khronos KTX2 texture container surfaced as a read-only pseudo-archive " +
    "(FULL + header metadata + per-mip-level raw blobs + key/value data); " +
    "supercompressed level data is exposed verbatim, never transcoded.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var file = ReadAll(stream);
    var entries = Ktx2Decomposer.Decompose(file);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();
  }

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
