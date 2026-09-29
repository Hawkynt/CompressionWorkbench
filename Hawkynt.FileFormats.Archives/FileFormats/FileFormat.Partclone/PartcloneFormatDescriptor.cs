#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Partclone;

/// <summary>
/// Descriptor for partclone — the Clonezilla backup format that
/// captures only allocated filesystem blocks alongside a per-block usage
/// bitmap. Listing surfaces the reconstructed disk image plus a
/// <c>metadata.ini</c> describing the source FS; extraction either writes the
/// raw <c>image.img</c> or, when the inner filesystem can be identified,
/// delegates to the matching descriptor so the user gets the original files.
/// Compressed partclone streams (LZ4/zstd) are not handled here — they're a
/// shell-pipe responsibility upstream of this format.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://partclone.org</c> — official partclone site</description></item>
///   <item><description><c>https://github.com/Thomas-Tsai/partclone</c> — canonical source — the image header is defined in src/partclone.h</description></item>
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
    // ASCII "partclone-image" at offset 0 — 15-byte unterminated literal.
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
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    stream.Position = 0;
    var reader = new PartcloneReader(stream);
    var info = reader.Info;
    var virtualSize = checked((long)(info.TotalBlocks * info.BlockSize));
    var physicalSize = checked((long)(info.UsedBlocks * info.BlockSize));
    var metaLen = BuildMetadataBytes(info).LongLength;
    var bitmapLen = reader.ReadAllocationMap().LongLength;

    return [
      new ArchiveEntryInfo(0, "metadata.ini", metaLen, metaLen, "stored", false, false, null),
      new ArchiveEntryInfo(1, "allocation.map", bitmapLen, bitmapLen, "stored", false, false, null),
      new ArchiveEntryInfo(2, "image.img",    virtualSize, physicalSize, "stored", false, false, null),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    stream.Position = 0;
    var reader = new PartcloneReader(stream);
    var info = reader.Info;

    var emitMeta = files == null || files.Length == 0 || MatchesFilter("metadata.ini", files);
    var emitMap = files == null || files.Length == 0 || MatchesFilter("allocation.map", files);
    var emitImg  = files == null || files.Length == 0 || MatchesFilter("image.img", files);

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

  /// <summary>Creates a Partclone v2 image from <c>image.img</c>, <c>metadata.ini</c>, and optionally <c>allocation.map</c>.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(static i => !i.IsDirectory).ToDictionary(static i => Path.GetFileName(i.ArchiveName.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
    if (!files.TryGetValue("image.img", out var image) || !files.TryGetValue("metadata.ini", out var metadata))
      throw new InvalidDataException("Partclone creation requires image.img and metadata.ini inputs.");
    var map = files.TryGetValue("allocation.map", out var allocation) ? allocation.ReadContent() : [];
    using var disk = image.InMemoryContent is { } content
      ? new MemoryStream(content, writable: false)
      : File.OpenRead(image.FullPath);
    PartcloneWriter.Write(output, disk, metadata.ReadContent(), map, options.FormatSpecific);
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
