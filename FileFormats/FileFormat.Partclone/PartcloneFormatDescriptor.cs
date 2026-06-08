#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Partclone;

/// <summary>
/// Read-only descriptor for partclone — the Clonezilla backup format that
/// captures only allocated filesystem blocks alongside a per-block usage
/// bitmap. Listing surfaces the reconstructed disk image plus a
/// <c>metadata.ini</c> describing the source FS; extraction either writes the
/// raw <c>image.img</c> or, when the inner filesystem can be identified,
/// delegates to the matching descriptor so the user gets the original files.
/// Compressed partclone streams (LZ4/zstd) are not handled here — they're a
/// shell-pipe responsibility upstream of this format.
/// </summary>
public sealed class PartcloneFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Partclone";
  public string DisplayName => "partclone (Clonezilla)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".aa";
  // .aa / .000 / .img extensions all collide with other formats, so detection
  // is magic-driven; the extension list is informative for the picker only.
  public IReadOnlyList<string> Extensions => [".aa", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "partclone-image" at offset 0 — 15-byte unterminated literal.
    new(PartcloneReader.Magic, Offset: 0, Confidence: 0.98),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Clonezilla / partclone filesystem-aware backup image — bitmap + only used blocks.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    stream.Position = 0;
    var reader = new PartcloneReader(stream);
    var info = reader.Info;
    var virtualSize = checked((long)(info.TotalBlocks * info.BlockSize));
    var physicalSize = checked((long)(info.UsedBlocks * info.BlockSize));
    var metaLen = BuildMetadataBytes(info).LongLength;

    return [
      new ArchiveEntryInfo(0, "metadata.ini", metaLen, metaLen, "stored", false, false, null),
      new ArchiveEntryInfo(1, "image.img",    virtualSize, physicalSize, "stored", false, false, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    stream.Position = 0;
    var reader = new PartcloneReader(stream);
    var info = reader.Info;

    var emitMeta = files == null || files.Length == 0 || MatchesFilter("metadata.ini", files);
    var emitImg  = files == null || files.Length == 0 || MatchesFilter("image.img", files);

    if (emitMeta)
      WriteFile(outputDir, "metadata.ini", BuildMetadataBytes(info));

    if (emitImg) {
      Directory.CreateDirectory(outputDir);
      var imgPath = Path.Combine(outputDir, "image.img");
      using var fs = File.Create(imgPath);
      reader.StreamDiskTo(fs);
    }
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
    sb.Append(CultureInfo.InvariantCulture, $"device_size = {info.DeviceSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bitmap_mode = {info.BitmapMode}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum_mode = {info.ChecksumMode}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum_size = {info.ChecksumSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"blocks_per_checksum = {info.BlocksPerChecksum}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bitmap_offset = {info.BitmapOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_offset = {info.DataOffset}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
