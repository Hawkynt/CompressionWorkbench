#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for NsPack-packed Win32 executables. NsPack
/// (LiuXingPing, early 2000s) is a Chinese PE compressor whose unpacker stub
/// renames sections to <c>".nsp0"</c>, <c>".nsp1"</c>, <c>".nsp2"</c>
/// (sometimes without the leading dot — <c>"nsp1"</c>, <c>"nsp2"</c>).
/// Many builds also embed the literal <c>"NsPack"</c> in the stub.
///
/// References:
/// <list type="bullet">
///   <item><description>NsPack by North Star / LiuXingPing — commercial distribution ceased; identified via RE-scene documentation</description></item>
///   <item><description><c>https://github.com/horsicq/Detect-It-Easy</c> — Detect It Easy — maintained packer-detection signature database (NsPack signatures)</description></item>
/// </list>
/// </summary>
public sealed class NsPackFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "NsPack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "NsPack (Win32 PE)";
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
    "NsPack (LiuXingPing) Win32 PE compressor — surfaces the .nsp0/.nsp1/.nsp2 " +
    "section table and the embedded \"NsPack\" literal. Decompression delegated " +
    "to generic PE unpackers (RL!dePacker, QUnpack, NsPackDie).";

  private static ReadOnlySpan<byte> NsPackLiteral => "NsPack"u8;

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
      throw new InvalidDataException("NsPack: not a valid PE.");

    var sections = PackerScanner.GetPeSections(bytes);
    var nspackSection = sections.FirstOrDefault(s =>
      s.Name.StartsWith("nsp", StringComparison.OrdinalIgnoreCase) ||
      s.Name.StartsWith(".nsp", StringComparison.OrdinalIgnoreCase));

    var nspackLitOffset = PackerScanner.IndexOfBounded(bytes, NsPackLiteral, 0x10000);

    if (string.IsNullOrEmpty(nspackSection.Name) && nspackLitOffset < 0)
      throw new InvalidDataException("NsPack: neither nsp* section nor 'NsPack' literal found.");

    return [
      ("metadata.ini", BuildMetadata(sections, nspackSection.Name, nspackLitOffset)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string nspackSectionName, int nspackLitOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[nspack]");
    sb.Append(CultureInfo.InvariantCulture, $"nspack_section = {(string.IsNullOrEmpty(nspackSectionName) ? "(none)" : nspackSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"nspack_literal_offset = {(nspackLitOffset < 0 ? "(not found)" : $"0x{nspackLitOffset:X4}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to RL!dePacker / QUnpack / NsPackDie\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
