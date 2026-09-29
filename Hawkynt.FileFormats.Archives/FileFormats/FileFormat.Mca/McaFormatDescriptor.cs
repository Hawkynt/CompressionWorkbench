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
///   <item><description><c>https://github.com/PaperMC/SectorTool/blob/master/SPECIFICATION.MD</c> — PaperMC — region sectors, timestamp semantics, compression ids, and external chunk files</description></item>
///   <item><description>No official Mojang specification — the layout is community-documented</description></item>
/// </list>
/// </summary>
public sealed class McaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
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
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
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
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("gzip", "Gzip"), new("zlib", "Zlib"), new("stored", "Stored"), new("lz4", "LZ4")
  ];
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

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Timestamp", "Default chunk timestamp", FormatOptionKind.Integer, "0", Description: "Unsigned Unix timestamp in seconds; may be overridden per chunk with Timestamp.X.Z."),
  ];

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
        OriginalSize: GetUncompressedSize(reader, c),
        CompressedSize: c.LengthBytes - 1,
        Method: (c.CompressionType & 0x7F) switch { 1 => "gzip", 2 => "zlib", 3 => "stored", 4 => "lz4", _ => "unknown" },
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: DateTime.UnixEpoch.AddSeconds(c.Timestamp)));
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
      var data = reader.ExtractChunkNbt(c);
      FormatHelpers.WriteFile(outputDir, name, data);
      File.SetLastWriteTimeUtc(Path.Combine(outputDir, name), DateTime.UnixEpoch.AddSeconds(c.Timestamp));
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
      var bytes = reader.ExtractChunkNbt(c);
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => McaWriter.Write(output, inputs, options);

  private static long GetUncompressedSize(McaReader reader, McaReader.ChunkEntry chunk) {
    try { return reader.ExtractChunkNbt(chunk).LongLength; }
    catch (NotSupportedException) { return -1; }
  }
}
