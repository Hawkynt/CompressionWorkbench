#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Srec;

/// <summary>
/// Pseudo-archive descriptor for Motorola S-record firmware files (S19/S28/S37).
/// Decodes the ASCII records into a flat binary and surfaces
/// <c>firmware.bin</c> + <c>metadata.ini</c>; <see cref="Create"/> re-encodes a
/// flat <c>firmware.bin</c> back into S-record text. Sibling of the Intel HEX
/// descriptor in <c>FileFormat.FirmwareHex</c>.
///
/// References:
/// <list type="bullet">
///   <item><description>Motorola "M68000 Family Programmer's Reference Manual" — S-record appendix (the defining document)</description></item>
///   <item><description><c>https://srecord.sourceforge.net</c> — SRecord tool suite — thorough format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/SREC_(file_format)</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class SrecFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Srec";
  public string DisplayName => "Motorola S-Record";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".s19";
  public IReadOnlyList<string> Extensions => [".s19", ".s28", ".s37", ".srec", ".mot", ".mhx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 'S0' is the standard header-record start. Low confidence — two ASCII chars
    // collide with other text formats — so extension dispatch is primary.
    new([(byte)'S', (byte)'0'], Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description =>
    "Motorola S-record ASCII firmware (S0 header + S1/S2/S3 data + S7/S8/S9 termination).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(ReadImage(stream)).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(ReadImage(stream))) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Re-encodes a flat firmware image (the <c>firmware.bin</c> input, or the
  /// single non-metadata input) as S-record text at base address 0. The address
  /// width is auto-selected from the image size.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var payload = SelectPayload(inputs);
    var text = SrecWriter.Write(payload);
    var bytes = Encoding.ASCII.GetBytes(text);
    output.Write(bytes, 0, bytes.Length);
  }

  private static byte[] SelectPayload(IReadOnlyList<ArchiveInputInfo> inputs) {
    var files = inputs.Where(i => !i.IsDirectory).ToList();
    var firmware = files.FirstOrDefault(i =>
      Path.GetFileName(i.ArchiveName).Equals("firmware.bin", StringComparison.OrdinalIgnoreCase));
    firmware ??= files.FirstOrDefault(i =>
      !Path.GetFileName(i.ArchiveName).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    firmware ??= files.FirstOrDefault();
    return firmware?.ReadContent() ?? [];
  }

  private static SrecImage ReadImage(Stream stream) {
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    return SrecReader.Read(reader.ReadToEnd());
  }

  private static List<(string Name, byte[] Data)> BuildEntries(SrecImage image) =>
  [
    ("metadata.ini", BuildMetadata(image)),
    ("firmware.bin", image.ToFlatBinary()),
  ];

  private static byte[] BuildMetadata(SrecImage image) {
    var sb = new StringBuilder();
    sb.AppendLine("[srec]");
    sb.Append(CultureInfo.InvariantCulture, $"record_count = {image.RecordCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_record_count = {image.DataRecordCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"segment_count = {image.Segments.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_data_bytes = {image.TotalDataBytes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"base_address = 0x{image.BaseAddress:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"start_address = {(image.StartAddress.HasValue ? $"0x{image.StartAddress.Value:X8}" : "(unspecified)")}\n");
    for (var i = 0; i < image.Segments.Count; i++) {
      var (a, d) = image.Segments[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"segment_{i} = 0x{a:X8} .. 0x{a + (uint)d.Length:X8} ({d.Length} bytes)\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
