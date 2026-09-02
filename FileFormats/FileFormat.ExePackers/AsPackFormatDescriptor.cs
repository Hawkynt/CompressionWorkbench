#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for ASPack-packed Win32 executables. ASPack
/// (Solodovnikov, late 1990s) is a long-running Win32 PE compressor whose
/// unpacker stub renames at least one section to <c>".aspack"</c> or
/// <c>".adata"</c> and almost always embeds the literal <c>"ASPack"</c>
/// somewhere in the first 64 KB of the file. The compression core is ASPack's
/// own LZ77-plus-Huffman stream, not aPLib as is widely repeated; see
/// <see cref="AsPackLzDecoder"/>, which
/// <see cref="AsPackExecutablePackerHandler"/> uses to unpack the image.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://www.aspack.com</c> — official ASPack site (ASPack Software)</description></item>
///   <item><description><c>https://github.com/horsicq/Detect-It-Easy</c> — Detect It Easy — maintained packer-detection signature database</description></item>
/// </list>
/// </summary>
public sealed class AsPackFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "AsPack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ASPack (Win32 PE)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".exe";
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
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
    "ASPack (Solodovnikov, 1998+) Win32 PE compressor — surfaces section " +
    "table and the embedded \"ASPack\" literal. Payload decompression is " +
    "handled in process by the aspack executable-packer handler.";

  private static ReadOnlySpan<byte> AsPackLiteral => "ASPack"u8;
  private static ReadOnlySpan<byte> APLibLiteral => "aPLib"u8;

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();

    if (!PackerScanner.IsPe(bytes))
      throw new InvalidDataException("ASPack: not a valid PE.");

    var sections = PackerScanner.GetPeSections(bytes);
    var aspackSection = sections.FirstOrDefault(s =>
      s.Name.Equals(".aspack", StringComparison.OrdinalIgnoreCase) ||
      s.Name.Equals(".adata", StringComparison.OrdinalIgnoreCase));

    var asPackLitOffset = PackerScanner.IndexOfBounded(bytes, AsPackLiteral, 0x10000);
    var aplibLitOffset = PackerScanner.IndexOfBounded(bytes, APLibLiteral, 0x10000);

    if (string.IsNullOrEmpty(aspackSection.Name) && asPackLitOffset < 0)
      throw new InvalidDataException("ASPack: neither .aspack/.adata section nor 'ASPack' literal found.");

    return [
      ("metadata.ini", BuildMetadata(sections, aspackSection.Name, asPackLitOffset, aplibLitOffset)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string aspackSectionName, int asPackLitOffset, int aplibLitOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[aspack]");
    sb.Append(CultureInfo.InvariantCulture, $"aspack_section = {(string.IsNullOrEmpty(aspackSectionName) ? "(none)" : aspackSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"aspack_literal_offset = {(asPackLitOffset < 0 ? "(not found)" : $"0x{asPackLitOffset:X4}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"aplib_literal_offset = {(aplibLitOffset < 0 ? "(not found)" : $"0x{aplibLitOffset:X4}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = payload decompression is available through the aspack executable-packer handler\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
