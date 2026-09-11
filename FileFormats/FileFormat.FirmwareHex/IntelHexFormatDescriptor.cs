#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Pseudo-archive descriptor for Intel HEX firmware files. Decodes the ASCII
/// records into a flat binary (<c>firmware.bin</c>) and surfaces a
/// <c>metadata.ini</c> with record count, declared start address, and sparse
/// segment layout.
///
/// References:
/// <list type="bullet">
///   <item><description>Intel "Hexadecimal Object File Format Specification", Rev. A (1988) — the defining document</description></item>
///   <item><description><c>https://developerhelp.microchip.com/xwiki/bin/view/software-tools/ipe/sqtp-file-format-specification/intel-hex/</c> — record layout and checksum rules</description></item>
/// </list>
/// </summary>
public sealed class IntelHexFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveDefragmentable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "IntelHex";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Intel HEX";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".hex";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".hex", ".ihex", ".ihx", ".h86"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ':' is the universal start-of-record marker. Low confidence because many
    // text formats happen to start with ':'; extension-based detection is the
    // primary dispatch path.
    new([(byte)':'], Confidence: 0.20),
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
    "Intel HEX ASCII firmware records (data/ESA/SSA/ELA/SLA); used by EPROM/flash programmers.";

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
  /// Writes a fresh Intel HEX file: the single payload input becomes the data
  /// records, and a <c>metadata.ini</c> alongside it -- the one this descriptor's
  /// own reader renders -- supplies the sparse segment map and start-address form
  /// that a flat binary cannot carry.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    FirmwareHexWriter.WriteIntelHex(output, FirmwareHexWriter.ImageFrom(inputs, "IntelHex"));
  }

  /// <summary>
  /// Removes all programmed bytes and start-address state while leaving the
  /// canonical valid empty Intel HEX document (the EOF record). This overrides
  /// the generic pseudo-archive purge because <c>firmware.bin</c> is a rendered
  /// view that also exists as a zero-length view of an empty image.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Intel HEX purge requires a readable, writable, seekable stream.", nameof(archive));

    archive.Position = 0;
    _ = BuildEntries(archive); // validate before committing a destructive edit

    using var rebuilt = new MemoryStream();
    FirmwareHexWriter.WriteIntelHex(rebuilt,
      new FirmwareImage([], StartAddress: null, RecordCount: 0, GapCount: 0, TotalDataBytes: 0, SourceFormat: "IntelHex"));
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
    archive.Position = 0;
  }

  // IArchiveModifiable and IArchiveDefragmentable intentionally use their shared
  // verified staged-rebuild implementations. Rebuilding is the native edit model
  // for a line-oriented HEX file: there are no allocation structures to patch in
  // place, and the rendered metadata now preserves sparse address runs and the
  // distinction between type-03 CS:IP and type-05 linear start records.

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    var text = reader.ReadToEnd();
    var image = IntelHexReader.Read(text);
    return FirmwareHexCommon.BuildEntries(image);
  }
}
