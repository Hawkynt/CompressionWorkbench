#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Partclone;

/// <summary>
/// Descriptor for partclone — the Clonezilla backup format that
/// captures only allocated filesystem blocks alongside a per-block usage
/// bitmap. Listing surfaces the reconstructed partition as <c>image.img</c>,
/// the serialized <c>allocation.map</c> and a <c>metadata.ini</c> describing
/// the source FS; opening the files inside <c>image.img</c> is left to the
/// matching file-system descriptor. Compressed partclone streams (LZ4/zstd) are not handled here — they're a
/// shell-pipe responsibility upstream of this format.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://partclone.org</c> — official partclone site</description></item>
///   <item><description><c>https://github.com/Thomas-Tsai/partclone/blob/master/IMAGE_FORMATS.md</c> — partclone — image format 0001/0002 layout</description></item>
///   <item><description><c>https://github.com/Thomas-Tsai/partclone</c> — canonical source (GPL, used as specification and as the partclone.chkimg/restore oracle only)</description></item>
///   <item><description><c>https://clonezilla.org</c> — Clonezilla — primary consumer of partclone images</description></item>
/// </list>
/// </summary>
public sealed class PartcloneFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Partclone";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "partclone (Clonezilla)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".aa";
  // .aa / .000 / .img extensions all collide with other formats, so detection
  // is magic-driven; the extension list is informative for the picker only.
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".aa", ".img"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "partclone-image" at offset 0 (the 16-byte field ends in a NUL).
    new(PartcloneReader.Magic, Offset: 0, Confidence: 0.98),
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
    "Clonezilla / partclone filesystem-aware backup image — bitmap + only used blocks.";

  /// <summary>
  /// Lists <c>metadata.ini</c>, <c>allocation.map</c> and the reconstructed <c>image.img</c>.
  /// </summary>
  /// <remarks>
  /// Never throws for a damaged or unsupported image: it then lists only a <c>metadata.ini</c>
  /// carrying <c>parse_status=partial</c> and the reason.
  /// </remarks>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    stream.Position = 0;
    PartcloneReader reader;
    try {
      reader = new PartcloneReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      var partial = BuildFailureMetadata(ex);
      return [new ArchiveEntryInfo(0, "metadata.ini", partial.Length, partial.Length, "stored", false, false, null)];
    }
    var info = reader.Info;
    var virtualSize = checked((long)(info.TotalBlocks * info.BlockSize));
    var physicalSize = checked((long)(info.UsedBlocks * info.BlockSize));
    var metaLen = BuildMetadataBytes(info).LongLength;
    var bitmapLen = info.BitmapMode switch {
      PartcloneReader.BmBit => (long)((info.TotalBlocks + 7) / 8),
      PartcloneReader.BmByte => (long)info.TotalBlocks,
      _ => 0L,
    };

    return [
      new ArchiveEntryInfo(0, "metadata.ini", metaLen, metaLen, "stored", false, false, null),
      new ArchiveEntryInfo(1, "allocation.map", bitmapLen, bitmapLen, "stored", false, false, null),
      new ArchiveEntryInfo(2, "image.img", virtualSize, physicalSize, "stored", false, false, null),
    ];
  }

  /// <summary>
  /// Extracts the requested entries. Header, bitmap and data checksums are verified, so a damaged
  /// image throws <see cref="InvalidDataException"/> (after writing <c>metadata.ini</c> if asked).
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    stream.Position = 0;
    var all = files == null || files.Length == 0;
    var emitMeta = all || MatchesFilter("metadata.ini", files!);
    var emitMap = all || MatchesFilter("allocation.map", files!);
    var emitImg = all || MatchesFilter("image.img", files!);

    PartcloneReader reader;
    try {
      reader = new PartcloneReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      if (emitMeta) WriteFile(outputDir, "metadata.ini", BuildFailureMetadata(ex));
      if (emitMap || emitImg) throw;
      return;
    }
    var info = reader.Info;

    if (emitMeta)
      WriteFile(outputDir, "metadata.ini", BuildMetadataBytes(info));

    if (emitMap)
      WriteFile(outputDir, "allocation.map", reader.ReadAllocationMap());

    if (emitImg) {
      Directory.CreateDirectory(outputDir);
      var imgPath = Path.Combine(outputDir, "image.img");
      using var fs = File.Create(imgPath);
      reader.StreamDiskTo(fs);
    }
  }

  /// <summary>
  /// Creates a partclone 0002 image from <c>image.img</c> plus, optionally, the <c>metadata.ini</c> and
  /// <c>allocation.map</c> an extraction produced. Without the map, only non-zero blocks are stored.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var files = new Dictionary<string, ArchiveInputInfo>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = Path.GetFileName(input.ArchiveName.Replace('\\', '/'));
      if (name is not ("image.img" or "metadata.ini" or "allocation.map"))
        throw new ArgumentException($"Partclone images hold one partition: expected image.img with optional metadata.ini and allocation.map, got '{input.ArchiveName}'.", nameof(inputs));
      if (!files.TryAdd(name, input))
        throw new ArgumentException($"Duplicate partclone input '{name}'.", nameof(inputs));
    }
    if (!files.TryGetValue("image.img", out var image))
      throw new ArgumentException("Partclone creation requires an image.img input.", nameof(inputs));
    var metadata = files.TryGetValue("metadata.ini", out var m) ? m.ReadContent() : [];
    var map = files.TryGetValue("allocation.map", out var allocation) ? allocation.ReadContent() : [];
    using Stream disk = image.InMemoryContent is { } content
      ? new MemoryStream(content, writable: false)
      : File.OpenRead(image.FullPath);
    PartcloneWriter.Write(output, disk, metadata, map, options.FormatSpecific);
  }

  private static byte[] BuildFailureMetadata(Exception ex) {
    var sb = new StringBuilder();
    sb.Append("[partclone]\n");
    sb.Append("parse_status = partial\n");
    sb.Append("error = ").Append(ex.Message.Replace('\n', ' ')).Append('\n');
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildMetadataBytes(PartcloneReader.PartcloneImage info) {
    var sb = new StringBuilder();
    sb.AppendLine("[partclone]");
    sb.Append("ptc_version = ").AppendLine(info.PtcVersion);
    sb.Append(CultureInfo.InvariantCulture, $"image_version = {info.ImageVersion}\n");
    sb.Append("fs = ").AppendLine(info.FsType);
    sb.Append(CultureInfo.InvariantCulture, $"block_size = {info.BlockSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_blocks = {info.TotalBlocks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"used_blocks = {info.UsedBlocks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"superblock_used_blocks = {info.SuperBlockUsedBlocks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"device_size = {info.DeviceSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bitmap_mode = {info.BitmapMode}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum_mode = {info.ChecksumMode}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum_size = {info.ChecksumSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"blocks_per_checksum = {info.BlocksPerChecksum}\n");
    sb.Append(CultureInfo.InvariantCulture, $"cpu_bits = {info.CpuBits}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reseed_checksum = {info.ReseedChecksum}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bitmap_offset = {info.BitmapOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_offset = {info.DataOffset}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
