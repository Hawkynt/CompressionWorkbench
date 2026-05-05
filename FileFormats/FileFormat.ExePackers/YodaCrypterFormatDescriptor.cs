#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Yoda's Crypter / Yoda's Protector packed
/// Win32 executables. Yoda's Crypter (Ashkbiz Danehkar, early 2000s) is a
/// classic anti-RE crypter whose unpacker stub renames at least one section
/// to <c>".yC"</c> or <c>"yC"</c> and embeds the literal copyright string
/// <c>"Yoda's Crypter"</c> (or simply <c>"Yoda's"</c>) somewhere in the file.
/// </summary>
public sealed class YodaCrypterFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "YodaCrypter";
  public string DisplayName => "Yoda's Crypter (Win32 PE)";
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
    "Yoda's Crypter / Yoda's Protector (Ashkbiz Danehkar) Win32 PE crypter — " +
    "surfaces the .yC/yC section table and the embedded \"Yoda's\" copyright. " +
    "Decompression delegated to Yoda's Crypter Unpacker / generic PE unpackers.";

  private static ReadOnlySpan<byte> YodasLiteral => "Yoda's"u8;

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
      throw new InvalidDataException("Yoda's Crypter: not a valid PE.");

    var sections = PackerScanner.GetPeSections(bytes);
    var ycSection = sections.FirstOrDefault(s =>
      s.Name.Equals(".yC", StringComparison.Ordinal) ||
      s.Name.Equals("yC", StringComparison.Ordinal));

    var yodasLitOffset = PackerScanner.IndexOfBounded(bytes, YodasLiteral, 0x10000);

    if (string.IsNullOrEmpty(ycSection.Name) && yodasLitOffset < 0)
      throw new InvalidDataException("Yoda's Crypter: neither .yC/yC section nor 'Yoda's' literal found.");

    return [
      ("metadata.ini", BuildMetadata(sections, ycSection.Name, yodasLitOffset)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string ycSectionName, int yodasLitOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[yoda_crypter]");
    sb.Append(CultureInfo.InvariantCulture, $"yc_section = {(string.IsNullOrEmpty(ycSectionName) ? "(none)" : ycSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"yodas_literal_offset = {(yodasLitOffset < 0 ? "(not found)" : $"0x{yodasLitOffset:X4}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to Yoda's Crypter Unpacker / RL!dePacker / QUnpack\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
