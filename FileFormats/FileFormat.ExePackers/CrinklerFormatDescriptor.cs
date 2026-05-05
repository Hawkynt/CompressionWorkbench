#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Crinkler-packed Win32 executables. Crinkler
/// (Mentor &amp; Blueberry, 2005+) is the de facto 4K Windows executable
/// compressor of the demoscene; it produces extremely small, atypically-laid
/// out PE files (often only 1-2 sections, no real import directory) and
/// embeds the literal string <c>"Crinkler"</c> somewhere in the file.
/// </summary>
/// <remarks>
/// Structural-only detection (small/weird PE) is unreliable, so we require
/// the embedded literal in addition to <see cref="PackerScanner.IsPe"/>.
/// </remarks>
public sealed class CrinklerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Crinkler";
  public string DisplayName => "Crinkler (Win32 PE 4K)";
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
    "Crinkler (Mentor & Blueberry) — 4K Win32 PE compressor used in the " +
    "Windows demoscene. Detection by embedded \"Crinkler\" literal in an " +
    "atypical PE. Decompression delegated to the original Crinkler linker " +
    "(/REPORT) or to crinkler-disasm.";

  private static ReadOnlySpan<byte> CrinklerLiteralUpper => "Crinkler"u8;
  private static ReadOnlySpan<byte> CrinklerLiteralLower => "crinkler"u8;
  private static ReadOnlySpan<byte> CrinklerLiteralAllCaps => "CRINKLER"u8;

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
      throw new InvalidDataException("Crinkler: not a valid PE.");

    var span = bytes.AsSpan();
    var idx = span.IndexOf(CrinklerLiteralUpper);
    if (idx < 0) idx = span.IndexOf(CrinklerLiteralLower);
    if (idx < 0) idx = span.IndexOf(CrinklerLiteralAllCaps);
    if (idx < 0)
      throw new InvalidDataException("Crinkler: \"Crinkler\" literal not found anywhere in file.");

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
    sb.AppendLine("[crinkler]");
    sb.Append(CultureInfo.InvariantCulture, $"crinkler_literal_offset = 0x{literalOffset:X6}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {totalSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to the Crinkler linker (/REPORT) or crinkler-disasm\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
