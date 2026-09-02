#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ffu;

/// <summary>
/// FFU (Windows Full Flash Update) image. The layout is a Security Header
/// (signature <c>"SignedImage\0"</c>, u32 chunk size in KiB, u32 header length,
/// u32 catalog size, u32 hash-table size), followed by the catalog + hash table,
/// then an Image Header (signature <c>"ImageFlash  "</c>, u32 manifest length,
/// u32 chunk size), the manifest text, and one or more Store Headers describing the
/// flashable payload split into fixed-size chunks driven by a block-data / locations
/// table.
///
/// <para>This descriptor surfaces a verbatim <c>FULL.ffu</c>, a <c>metadata.ini</c>
/// (signature validity, chunk size, catalog / hash-table / manifest sizes, computed
/// header span and payload offset/length) and a single structural <c>payload.bin</c>
/// entry covering the flash-data region after the headers. Per-chunk reconstruction
/// against the store's write-descriptor / block-locations table is deferred (it
/// requires the full store-header v1/v2 disk-location decode); this is documented in
/// the metadata via <c>chunk_reconstruction=deferred</c>. Read-only; malformed input
/// degrades to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>Microsoft "FFU image format" documentation (Windows Hardware manufacturing docs; originally published for Windows Phone imaging)</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/windows-hardware/manufacture/</c> — Windows manufacturing documentation portal</description></item>
/// </list>
/// </summary>
public sealed class FfuFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ffu";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Windows Full Flash Update (FFU)";
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
public string DefaultExtension => ".ffu";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ffu"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SignedImage\0"u8.ToArray(), Confidence: 0.95),
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
    "Windows Full Flash Update (FFU): Security Header (SignedImage) + catalog/hash + Image Header " +
    "(ImageFlash) + manifest + Store Header(s) + chunked payload. Surfaces FULL.ffu, metadata.ini " +
    "and a structural payload.bin; per-chunk store reconstruction is deferred. Read-only.";

  private static ReadOnlySpan<byte> SecuritySig => "SignedImage\0"u8;
  private static ReadOnlySpan<byte> ImageSig => "ImageFlash  "u8;

  private sealed record FfuModel(
    bool Valid,
    bool Partial,
    uint ChunkSizeKib,
    uint SecurityHeaderLength,
    uint CatalogSize,
    uint HashTableSize,
    bool ImageHeaderFound,
    uint ManifestLength,
    long PayloadOffset,
    long PayloadLength,
    string? ManifestText);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    var model = Parse(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.ffu", data.Length, data.Length, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    };
    var idx = 2;
    if (model.Valid && model.PayloadOffset > 0 && model.PayloadLength > 0)
      entries.Add(new ArchiveEntryInfo(idx++, "payload.bin", model.PayloadLength, model.PayloadLength, "Stored", false, false, null, Kind: "Track"));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.ffu"))
      WriteFile(outputDir, "FULL.ffu", data);

    var model = Parse(data);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(model)));

    if (model.Valid && model.PayloadOffset > 0 && model.PayloadLength > 0 &&
        model.PayloadOffset + model.PayloadLength <= data.Length && Wants(files, "payload.bin")) {
      var slab = new byte[model.PayloadLength];
      Array.Copy(data, model.PayloadOffset, slab, 0, model.PayloadLength);
      WriteFile(outputDir, "payload.bin", slab);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static FfuModel Parse(byte[] data) {
    try {
      if (data.Length < 32 || !data.AsSpan(0, SecuritySig.Length).SequenceEqual(SecuritySig))
        return Invalid();

      // Security Header (little-endian): signature[12], chunkSizeInKb (u32),
      // hashTableSize... layout per WP/ARM FFU v1: cbSize(u32), signature[12],
      // dwChunkSizeInKb(u32), dwAlgId(u32), dwCatalogSize(u32), dwHashTableSize(u32).
      // We treat offset 0 as the signature start (some tools place cbSize first);
      // probe both arrangements and pick the one whose fields stay in-bounds.
      var (chunkKib, catalogSize, hashSize, secLen, ok) = ReadSecurityFields(data);
      if (!ok) {
        // Still a valid signature; expose partial.
        return new FfuModel(true, true, chunkKib, secLen, catalogSize, hashSize, false, 0, 0, 0, null);
      }

      var imageHeaderOffset = secLen;
      var imageFound = false;
      uint manifestLen = 0;
      string? manifestText = null;
      long payloadOffset = 0;

      if (imageHeaderOffset >= 0 && imageHeaderOffset + 24 <= data.Length &&
          ContainsImageSig(data, imageHeaderOffset, out var imgPos)) {
        imageFound = true;
        // Image Header: signature[12], dwManifestLength(u32), dwChunkSize(u32).
        manifestLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(imgPos + 12, 4));
        var manifestOffset = imgPos + 20;
        if (manifestLen is > 0 and < (16 * 1024 * 1024) && manifestOffset + manifestLen <= data.Length) {
          manifestText = SafeAscii(data.AsSpan(manifestOffset, (int)manifestLen));
          // Payload begins after the manifest, rounded up to the chunk boundary.
          var afterManifest = manifestOffset + (long)manifestLen;
          var chunkBytes = chunkKib == 0 ? 0L : (long)chunkKib * 1024;
          payloadOffset = chunkBytes > 0 ? RoundUp(afterManifest, chunkBytes) : afterManifest;
        }
      }

      var payloadLen = payloadOffset > 0 && payloadOffset < data.Length ? data.Length - payloadOffset : 0;
      var partial = !imageFound || payloadOffset == 0;
      return new FfuModel(true, partial, chunkKib, secLen, catalogSize, hashSize,
        imageFound, manifestLen, payloadOffset, payloadLen, manifestText);
    } catch {
      return new FfuModel(true, true, 0, 0, 0, 0, false, 0, 0, 0, null);
    }
  }

  // The signature can sit at offset 0 (signature-first) or offset 4 (cbSize-first).
  // Return chunk size, catalog size, hash-table size and the computed security-header
  // length (rounded up to the chunk boundary), plus an ok flag.
  private static (uint Chunk, uint Catalog, uint Hash, uint SecLen, bool Ok) ReadSecurityFields(byte[] data) {
    var baseOff = data.AsSpan(0, SecuritySig.Length).SequenceEqual(SecuritySig) ? 0 : 4;
    var p = baseOff + 12; // past signature
    if (p + 16 > data.Length) return (0, 0, 0, 0, false);
    var chunkKib = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
    // algId at p+4
    var catalogSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 8, 4));
    var hashSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 12, 4));

    var fixedLen = (uint)(p + 16);
    long rawLen = (long)fixedLen + catalogSize + hashSize;
    if (chunkKib is 0 or > (64 * 1024) || catalogSize > 64 * 1024 * 1024 || hashSize > 256 * 1024 * 1024)
      return (chunkKib, catalogSize, hashSize, fixedLen, false);
    if (rawLen <= 0 || rawLen > data.Length) return (chunkKib, catalogSize, hashSize, fixedLen, false);

    var chunkBytes = (long)chunkKib * 1024;
    var secLen = RoundUp(rawLen, chunkBytes);
    if (secLen <= 0 || secLen >= data.Length) return (chunkKib, catalogSize, hashSize, (uint)Math.Min(rawLen, uint.MaxValue), false);
    return (chunkKib, catalogSize, hashSize, (uint)secLen, true);
  }

  private static bool ContainsImageSig(byte[] data, long startGuess, out int pos) {
    pos = 0;
    // Try the exact computed offset first, then scan a small window forward.
    var window = (int)Math.Min(data.Length, startGuess + (long)64 * 1024);
    for (var i = (int)Math.Max(0, startGuess); i + ImageSig.Length <= window; ++i) {
      if (data.AsSpan(i, ImageSig.Length).SequenceEqual(ImageSig)) { pos = i; return true; }
    }
    return false;
  }

  private static long RoundUp(long value, long align)
    => align <= 0 ? value : ((value + align - 1) / align) * align;

  private static FfuModel Invalid()
    => new(false, true, 0, 0, 0, 0, false, 0, 0, 0, null);

  private static string BuildMetadataIni(FfuModel m) {
    var sb = new StringBuilder();
    sb.Append("[Ffu]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(m.Valid ? 1 : 0)}\n");
    if (!m.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"chunk_size_kib={m.ChunkSizeKib}\n");
    sb.Append(CultureInfo.InvariantCulture, $"security_header_length={m.SecurityHeaderLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"catalog_size={m.CatalogSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"hash_table_size={m.HashTableSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_header_found={(m.ImageHeaderFound ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"manifest_length={m.ManifestLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_offset={m.PayloadOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_length={m.PayloadLength}\n");
    sb.Append("chunk_reconstruction=deferred\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(m.Partial ? "partial" : "ok")}\n");
    return sb.ToString();
  }

  private static string SafeAscii(ReadOnlySpan<byte> data) {
    try { return Encoding.ASCII.GetString(data); }
    catch { return string.Empty; }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
