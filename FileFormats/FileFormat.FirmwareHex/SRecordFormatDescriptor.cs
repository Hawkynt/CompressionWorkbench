#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Pseudo-archive descriptor for Motorola S-Record firmware files (S19/S28/S37).
/// Decodes the ASCII records into a flat binary and emits <c>firmware.bin</c> +
/// <c>metadata.ini</c>.
/// </summary>
public sealed class SRecordFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "SRecord";
  public string DisplayName => "Motorola S-Record";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".s19";
  public IReadOnlyList<string> Extensions => [".s19", ".s28", ".s37", ".srec", ".mot", ".mhx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 'S0' is the standard header record start — low confidence because two
    // ASCII characters can collide with other text formats.
    new([(byte)'S', (byte)'0'], Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description =>
    "Motorola S-Record ASCII firmware (S0 header + S1/S2/S3 data + S7/S8/S9 termination).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    FirmwareHexCommon.BuildArchiveEntries(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    var text = reader.ReadToEnd();
    var image = SRecordReader.Read(text);
    return FirmwareHexCommon.BuildEntries(image);
  }
}
