#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Mca;

/// <summary>
/// Surfaces a Minecraft region file (<c>.mca</c>) as an archive of per-chunk
/// decompressed NBT payloads. Each entry is named <c>chunk_X_Z.nbt</c> by its
/// in-region coordinates (0–31 in each axis). Unused chunk slots are skipped.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://minecraft.wiki/w/Region_file_format</c> — Minecraft Wiki — region/Anvil file layout (locations, timestamps, per-chunk compressed NBT)</description></item>
///   <item><description>No official Mojang specification — the layout is community-documented</description></item>
/// </list>
/// </summary>
public sealed class McaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Mca";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "MCA (Minecraft region)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".mca";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".mca", ".mcr"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // No stable byte-magic — the file starts with a raw location table that could be
  // almost anything. Resolved by extension only.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate/Gzip")];
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
  public string Description => "Minecraft region: per-chunk NBT payloads addressable by (X,Z) coordinate.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

  /// <summary>
  /// Opens a single chunk's decompressed NBT as a bounded read-only stream.
  /// The reader produces the decoded bytes per chunk; the matched bytes are
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the chunk's logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var reader = OpenReader(archive);
    foreach (var c in reader.Chunks) {
      var name = $"chunk_{c.RegionX}_{c.RegionZ}.nbt";
      if (!string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      byte[] bytes;
      try { bytes = reader.ExtractChunkNbt(c); }
      catch (NotSupportedException) { bytes = System.Array.Empty<byte>(); }
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  private static McaReader OpenReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new McaReader(ms.ToArray());
  }
}
