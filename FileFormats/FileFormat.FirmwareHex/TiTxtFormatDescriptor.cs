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
///
/// References:
/// <list type="bullet">
///   <item><description>Texas Instruments MSP430 programming/bootloader guides — define the TI-TXT format (@addr / data / q)</description></item>
///   <item><description><c>https://srecord.sourceforge.net</c> — SRecord tool suite — documents and converts TI-TXT (srec_ti_txt)</description></item>
/// </list>
/// </summary>
public sealed class TiTxtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveShrinkable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "TiTxt";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "TI-TXT (MSP430)";
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
  public string DefaultExtension => ".txt";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // TI-TXT begins with '@' on the first non-whitespace line; low confidence
    // because '@' shows up in lots of text formats (email/yaml/etc).
    new([(byte)'@'], Confidence: 0.15),
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
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "TI-TXT MSP430 firmware text (address/data/q-terminator).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    FirmwareHexCommon.BuildArchiveEntries(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Writes a fresh TI-TXT file: the single payload input becomes the data lines
  /// under an <c>@address</c> taken from a <c>metadata.ini</c> alongside it, and
  /// the file ends with the <c>q</c> the format requires.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    FirmwareHexWriter.WriteTiTxt(output, FirmwareHexWriter.ImageFrom(inputs, "TiTxt"));
  }

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    var text = reader.ReadToEnd();
    var image = TiTxtReader.Read(text);
    return FirmwareHexCommon.BuildEntries(image);
  }
}
