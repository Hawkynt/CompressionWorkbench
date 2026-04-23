#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UImage;

/// <summary>
/// Pseudo-archive descriptor for U-Boot legacy uImage containers (<c>mkimage</c>
/// output). Exposes <c>metadata.ini</c>, <c>header.bin</c> (the 64-byte legacy
/// header) and <c>payload.bin</c> (the compressed body verbatim). When the body
/// compression is <c>none</c> an additional <c>payload_decompressed.bin</c> is
/// emitted; for gzip/bzip2/lzma/lzo/lz4/zstd the body is left compressed and the
/// <c>metadata.ini</c> notes which scheme the caller needs to apply.
/// </summary>
public sealed class UImageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "UImage";
  public string DisplayName => "U-Boot uImage";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".uimg";
  /// <remarks>
  /// Extensions are intentionally empty — <c>.img</c>/<c>.bin</c>/<c>.uimg</c>
  /// are all overloaded by other firmware formats, so we rely on the distinctive
  /// <c>0x27051956</c> magic at offset 0 for detection.
  /// </remarks>
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x27, 0x05, 0x19, 0x56], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "U-Boot legacy uImage — 64-byte BE header + compressed body (kernel/ramdisk/fdt).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var img = UImageReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var entries = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadata(img), "stored"),
      ("header.bin", img.Header, "stored"),
    };
    if (img.Body.Length > 0) entries.Add(("payload.bin", img.Body, "stored"));

    // Only the identity ('none') scheme is handled inline to keep this project
    // dependency-light. For the real compression schemes the caller should feed
    // payload.bin through FileFormat.Gzip/Bzip2/Lzma/Lzop/Lz4/Zstd.
    if (img.Compression == 0 && img.Body.Length > 0)
      entries.Add(("payload_decompressed.bin", img.Body, "stored"));

    return entries;
  }

  private static byte[] BuildMetadata(UImageReader.UImage i) {
    var sb = new StringBuilder();
    sb.AppendLine("[uimage]");
    sb.Append(CultureInfo.InvariantCulture, $"magic = 0x{i.Magic:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"name = {i.Name}\n");
    sb.Append(CultureInfo.InvariantCulture, $"timestamp = {i.Timestamp}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_size = {i.DataSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"load_address = 0x{i.LoadAddress:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_point = 0x{i.EntryPoint:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"os = {i.Os} ({UImageReader.OsName(i.Os)})\n");
    sb.Append(CultureInfo.InvariantCulture, $"arch = {i.Architecture} ({UImageReader.ArchName(i.Architecture)})\n");
    sb.Append(CultureInfo.InvariantCulture, $"type = {i.Type} ({UImageReader.TypeName(i.Type)})\n");
    sb.Append(CultureInfo.InvariantCulture, $"comp = {i.Compression} ({UImageReader.CompressionName(i.Compression)})\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"header_crc_stored = 0x{i.HeaderCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"header_crc_computed = 0x{i.ComputedHeaderCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"header_crc_ok = {(i.HeaderCrc == i.ComputedHeaderCrc).ToString().ToLowerInvariant()}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"data_crc_stored = 0x{i.DataCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"data_crc_computed = 0x{i.ComputedDataCrc:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"data_crc_ok = {(i.DataCrc == i.ComputedDataCrc).ToString().ToLowerInvariant()}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
