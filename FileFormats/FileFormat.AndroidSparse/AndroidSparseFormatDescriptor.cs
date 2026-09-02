#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AndroidSparse;

/// <summary>
/// Android sparse image (<c>.simg</c> / <c>.img</c>), the on-disk form emitted by
/// <c>img2simg</c> and consumed by <c>simg2img</c> / <c>fastboot</c>. A 28-byte file
/// header (magic <c>0x3AFF26ED</c>, versions, header/chunk-header sizes, block size,
/// block/chunk counts, image checksum) precedes <c>total_chunks</c> chunk records;
/// each record's 12-byte header (<c>chunk_type</c>, reserved, <c>chunk_sz</c> in
/// blocks, <c>total_sz</c> in bytes) is followed by RAW literal data, a 4-byte FILL
/// pattern, or nothing for DONT_CARE / CRC32.
///
/// <para><see cref="List"/> / <see cref="Extract"/> expand the container to a single
/// <c>image.raw</c> plus a <c>metadata.ini</c> describing the geometry.
/// <see cref="Create"/> packs a raw image back into a sparse container, emitting
/// DONT_CARE for zero block-runs and RAW for everything else. Malformed input
/// degrades gracefully without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://android.googlesource.com/platform/system/core/+/refs/heads/main/libsparse/</c> — AOSP libsparse, the reference implementation (img2simg/simg2img)</description></item>
///   <item><description><c>sparse_format.h</c> in that directory — the defining header for the 28-byte file header and chunk records</description></item>
/// </list>
/// </summary>
public sealed class AndroidSparseFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "AndroidSparse";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Android Sparse Image";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".simg";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".simg", ".sparse"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x3A, 0xFF, 0x26, 0xED], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("sparse", "Android Sparse")];
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
    "Android sparse image (img2simg/simg2img): 28-byte header + RAW/FILL/DONT_CARE/CRC32 chunks. " +
    "Expands to image.raw + metadata.ini; Create packs a raw image back into sparse form.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", 0, 0, "sparse", false, false, null, Kind: "Tag"),
    };
    try {
      var header = AndroidSparseCodec.ParseHeader(data);
      entries.Add(new ArchiveEntryInfo(1, "image.raw", header.ExpandedLength, data.Length,
        "sparse", false, false, null, Kind: "Track"));
    } catch {
      // Not a valid sparse image — expose only metadata (parse_status=partial).
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    var valid = TryExpand(data, out var raw, out var meta);

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(meta));

    if (valid && Wants(files, "image.raw"))
      WriteFile(outputDir, "image.raw", raw);
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // The (single) file input is the raw image to sparsify. When several inputs
    // are supplied the first file wins — sparse images hold exactly one image.
    var raw = FilesOnly(inputs).Select(f => f.Data).FirstOrDefault() ?? [];
    var sparse = AndroidSparseCodec.Build(raw, AndroidSparseConstants.DefaultBlockSize);
    output.Write(sparse, 0, sparse.Length);
  }

  private static bool TryExpand(byte[] data, out byte[] raw, out string metadata) {
    try {
      var header = AndroidSparseCodec.ParseHeader(data);
      raw = AndroidSparseCodec.Expand(data);
      metadata = BuildMetadata(header, raw.Length, partial: raw.Length != header.ExpandedLength);
      return true;
    } catch {
      raw = [];
      metadata = "[AndroidSparse]\nvalid=0\nparse_status=partial\n";
      return false;
    }
  }

  private static string BuildMetadata(AndroidSparseHeader h, long expandedLen, bool partial) {
    var sb = new StringBuilder();
    sb.Append("[AndroidSparse]\n");
    sb.Append("valid=1\n");
    sb.Append(CultureInfo.InvariantCulture, $"major_version={h.MajorVersion}\n");
    sb.Append(CultureInfo.InvariantCulture, $"minor_version={h.MinorVersion}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_size={h.BlockSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_blocks={h.TotalBlocks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_chunks={h.TotalChunks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_checksum={h.ImageChecksum}\n");
    sb.Append(CultureInfo.InvariantCulture, $"expanded_length={expandedLen}\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(partial ? "partial" : "ok")}\n");
    return sb.ToString();
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
