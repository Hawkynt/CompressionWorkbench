#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for kkrunchy-packed Win32 executables. kkrunchy
/// (ryg / Farbrausch, ~2003) is the 64K Windows executable compressor used by
/// .kkrieger, fr-08, and most early-2000s Farbrausch 64K intros. Its
/// unpacker stub embeds the literal string <c>"kkrunchy"</c> somewhere in the
/// packed file.
/// </summary>
public sealed class KkrunchyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Kkrunchy";
  public string DisplayName => "kkrunchy (Win32 PE 64K)";
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
    "kkrunchy (ryg / Farbrausch) — 64K Win32 PE compressor used by .kkrieger " +
    "and most Farbrausch 64K intros. Detection by embedded \"kkrunchy\" " +
    "literal. Decompression delegated to the kkrunchy reference tool.";

  private static ReadOnlySpan<byte> KkrunchyLiteralLower => "kkrunchy"u8;
  private static ReadOnlySpan<byte> KkrunchyLiteralCap => "Kkrunchy"u8;
  private static ReadOnlySpan<byte> KkrunchyLiteralUpper => "KKRUNCHY"u8;

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
      throw new InvalidDataException("kkrunchy: not a valid PE.");

    var span = bytes.AsSpan();
    var idx = span.IndexOf(KkrunchyLiteralLower);
    if (idx < 0) idx = span.IndexOf(KkrunchyLiteralCap);
    if (idx < 0) idx = span.IndexOf(KkrunchyLiteralUpper);
    if (idx < 0)
      throw new InvalidDataException("kkrunchy: \"kkrunchy\" literal not found anywhere in file.");

    var sections = PackerScanner.GetPeSections(bytes);
    return [
      ("metadata.ini", BuildMetadata(sections, idx, bytes.Length)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      int literalOffset, int totalSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[kkrunchy]");
    sb.Append(CultureInfo.InvariantCulture, $"kkrunchy_literal_offset = 0x{literalOffset:X6}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {totalSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to the kkrunchy reference tool (Farbrausch)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
