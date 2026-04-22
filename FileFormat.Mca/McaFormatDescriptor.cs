#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Mca;

/// <summary>
/// Surfaces a Minecraft region file (<c>.mca</c>) as an archive of per-chunk
/// decompressed NBT payloads. Each entry is named <c>chunk_X_Z.nbt</c> by its
/// in-region coordinates (0–31 in each axis). Unused chunk slots are skipped.
/// </summary>
public sealed class McaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mca";
  public string DisplayName => "MCA (Minecraft region)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mca";
  public IReadOnlyList<string> Extensions => [".mca", ".mcr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // No stable byte-magic — the file starts with a raw location table that could be
  // almost anything. Resolved by extension only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate/Gzip")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minecraft region: per-chunk NBT payloads addressable by (X,Z) coordinate.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = OpenReader(stream);
    var entries = new List<ArchiveEntryInfo>(reader.Chunks.Count);
    for (var i = 0; i < reader.Chunks.Count; ++i) {
      var c = reader.Chunks[i];
      entries.Add(new ArchiveEntryInfo(
        Index: i,
        Name: $"chunk_{c.RegionX}_{c.RegionZ}.nbt",
        OriginalSize: c.LengthBytes,
        CompressedSize: c.LengthBytes,
        Method: c.CompressionType switch { 1 => "gzip", 2 => "zlib", _ => "stored" },
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = OpenReader(stream);
    foreach (var c in reader.Chunks) {
      var name = $"chunk_{c.RegionX}_{c.RegionZ}.nbt";
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(name, files))
        continue;
      try {
        var data = reader.ExtractChunkNbt(c);
        FormatHelpers.WriteFile(outputDir, name, data);
      } catch (NotSupportedException) {
        // Unknown compression type — skip quietly; metadata still listed the entry.
      }
    }
  }

  private static McaReader OpenReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new McaReader(ms.ToArray());
  }
}
