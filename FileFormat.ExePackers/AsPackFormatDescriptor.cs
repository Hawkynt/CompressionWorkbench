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
/// somewhere in the first 64 KB of the file. The compression core itself is
/// an aPLib-style LZ77, so an <c>"aPLib"</c> marker is occasionally present
/// near the entry point as well.
/// </summary>
public sealed class AsPackFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "AsPack";
  public string DisplayName => "ASPack (Win32 PE)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ASPack (Solodovnikov, 1998+) Win32 PE compressor — surfaces section " +
    "table and the embedded \"ASPack\" / aPLib literals. Decompression " +
    "delegated to the original ASPack tool, AspackDie, or generic PE " +
    "unpackers (RL!dePacker, QUnpack).";

  private static ReadOnlySpan<byte> AsPackLiteral => "ASPack"u8;
  private static ReadOnlySpan<byte> APLibLiteral => "aPLib"u8;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

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
    sb.Append("note = decompression delegated to the ASPack tool / AspackDie / RL!dePacker\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
