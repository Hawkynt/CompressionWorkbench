#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Pseudo-archive descriptor for the TI-TXT firmware text format used by MSP430.
/// Address lines (<c>@HHHH</c>) introduce contiguous byte runs; a single <c>q</c>
/// terminates the file. Extension is intentionally empty — <c>.txt</c> is far
/// too ambiguous — so detection relies on the first non-whitespace byte being
/// <c>@</c>.
/// </summary>
public sealed class TiTxtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "TiTxt";
  public string DisplayName => "TI-TXT (MSP430)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".txt";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // TI-TXT begins with '@' on the first non-whitespace line; low confidence
    // because '@' shows up in lots of text formats (email/yaml/etc).
    new([(byte)'@'], Confidence: 0.15),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description =>
    "TI-TXT MSP430 firmware text (address/data/q-terminator).";

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
    var image = TiTxtReader.Read(text);
    return FirmwareHexCommon.BuildEntries(image);
  }
}
