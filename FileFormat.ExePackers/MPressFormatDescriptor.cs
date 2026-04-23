#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for MPRESS-packed Win32 PE / Linux ELF
/// executables. MPRESS (MATCODE Software, ~2007) emits two characteristic
/// sections named <c>.MPRESS1</c> and <c>.MPRESS2</c> in PE files; in both
/// PE and ELF builds the unpacker stub also embeds the literal copyright
/// strings <c>"MPRESS"</c> / <c>"MATCODE"</c> in the first ~64 KB.
/// </summary>
public sealed class MPressFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "MPress";
  public string DisplayName => "MPRESS (Win32 PE / Linux ELF)";
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
    "MPRESS (MATCODE) Win32 PE / Linux ELF compressor — surfaces the " +
    ".MPRESS1/.MPRESS2 sections (PE) or embedded MATCODE/MPRESS literal " +
    "(ELF). Decompression delegated to the official `mpress -d` tool.";

  private static ReadOnlySpan<byte> MPressLiteral => "MPRESS"u8;
  private static ReadOnlySpan<byte> MatcodeLiteral => "MATCODE"u8;

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

    var isPe = PackerScanner.IsPe(bytes);
    var isElf = bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F';
    var isMz = PackerScanner.IsMzExecutable(bytes);

    if (!isPe && !isElf && !isMz)
      throw new InvalidDataException("MPRESS: not a PE, MZ, or ELF executable.");

    var sections = isPe
      ? PackerScanner.GetPeSections(bytes)
      : (IReadOnlyList<(string Name, uint Characteristics)>)[];
    var mpressSection = sections.FirstOrDefault(s =>
      s.Name.StartsWith(".MPRESS", StringComparison.OrdinalIgnoreCase));

    var mpressLitOffset = PackerScanner.IndexOfBounded(bytes, MPressLiteral, 0x10000);
    var matcodeLitOffset = PackerScanner.IndexOfBounded(bytes, MatcodeLiteral, 0x10000);

    if (string.IsNullOrEmpty(mpressSection.Name) && mpressLitOffset < 0 && matcodeLitOffset < 0)
      throw new InvalidDataException("MPRESS: neither .MPRESS section nor MPRESS/MATCODE literal found.");

    return [
      ("metadata.ini", BuildMetadata(isPe, isElf, sections, mpressSection.Name, mpressLitOffset, matcodeLitOffset)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(bool isPe, bool isElf,
      IReadOnlyList<(string Name, uint Characteristics)> sections,
      string mpressSectionName, int mpressLitOffset, int matcodeLitOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[mpress]");
    sb.Append(CultureInfo.InvariantCulture, $"container = {(isPe ? "PE" : isElf ? "ELF" : "MZ")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"mpress_section = {(string.IsNullOrEmpty(mpressSectionName) ? "(none)" : mpressSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"mpress_literal_offset = {(mpressLitOffset < 0 ? "(not found)" : $"0x{mpressLitOffset:X4}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"matcode_literal_offset = {(matcodeLitOffset < 0 ? "(not found)" : $"0x{matcodeLitOffset:X4}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to `mpress -d` (official MATCODE tool)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
