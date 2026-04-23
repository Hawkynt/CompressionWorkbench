#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Pseudo-archive descriptor for Intel HEX firmware files. Decodes the ASCII
/// records into a flat binary (<c>firmware.bin</c>) and surfaces a
/// <c>metadata.ini</c> with record count, declared start address, and gap count.
/// </summary>
public sealed class IntelHexFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "IntelHex";
  public string DisplayName => "Intel HEX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hex";
  public IReadOnlyList<string> Extensions => [".hex", ".ihex", ".ihx", ".h86"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ':' is the universal start-of-record marker. Low confidence because many
    // text formats happen to start with ':'; extension-based detection is the
    // primary dispatch path.
    new([(byte)':'], Confidence: 0.20),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description =>
    "Intel HEX ASCII firmware records (data/ESA/SSA/ELA/SLA); used by EPROM/flash programmers.";

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
    var image = IntelHexReader.Read(text);
    return FirmwareHexCommon.BuildEntries(image);
  }
}
