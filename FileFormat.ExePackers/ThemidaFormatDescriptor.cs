#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Themida / WinLicense protected Win32
/// executables. Themida (Oreans Technologies, 2003+) is a heavyweight
/// commercial protector that combines virtualization, code mutation, and
/// anti-debug. Detection is best-effort: many builds wipe section names and
/// strip identifying strings, but the literal <c>"Themida"</c>,
/// <c>"ThemidaSDK"</c>, or <c>"WinLicense"</c> is frequently left in the
/// build for licensing/runtime calls. Some builds also leave a
/// <c>".themida"</c> section name.
/// </summary>
public sealed class ThemidaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Themida";
  public string DisplayName => "Themida / WinLicense (Win32 PE)";
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
    "Themida / WinLicense (Oreans) Win32 PE protector — best-effort detection " +
    "via embedded \"Themida\" / \"WinLicense\" literals or .themida sections. " +
    "Decompression delegated to UnThemida / Themida-Unpacker / x64dbg ScyllaHide " +
    "(no automated round-trip; protection is virtualization-based).";

  private static ReadOnlySpan<byte> ThemidaLiteral => "Themida"u8;
  private static ReadOnlySpan<byte> WinLicenseLiteral => "WinLicense"u8;
  private static ReadOnlySpan<byte> ThemidaSdkLiteral => "ThemidaSDK"u8;

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
      throw new InvalidDataException("Themida: not a valid PE.");

    var span = bytes.AsSpan();
    var themidaIdx = span.IndexOf(ThemidaLiteral);
    var winLicenseIdx = span.IndexOf(WinLicenseLiteral);
    var themidaSdkIdx = span.IndexOf(ThemidaSdkLiteral);

    if (themidaIdx < 0 && winLicenseIdx < 0)
      throw new InvalidDataException("Themida: neither 'Themida' nor 'WinLicense' literal found.");

    var sections = PackerScanner.GetPeSections(bytes);
    var themidaSection = sections.FirstOrDefault(s =>
      s.Name.StartsWith(".themida", StringComparison.OrdinalIgnoreCase));

    return [
      ("metadata.ini", BuildMetadata(sections, themidaSection.Name, themidaIdx, winLicenseIdx, themidaSdkIdx)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string themidaSectionName, int themidaIdx, int winLicenseIdx, int themidaSdkIdx) {
    var sb = new StringBuilder();
    sb.AppendLine("[themida]");
    sb.Append(CultureInfo.InvariantCulture, $"themida_section = {(string.IsNullOrEmpty(themidaSectionName) ? "(none)" : themidaSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"themida_literal_offset = {(themidaIdx < 0 ? "(not found)" : $"0x{themidaIdx:X6}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"winlicense_literal_offset = {(winLicenseIdx < 0 ? "(not found)" : $"0x{winLicenseIdx:X6}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"themida_sdk_literal_offset = {(themidaSdkIdx < 0 ? "(not found)" : $"0x{themidaSdkIdx:X6}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to UnThemida / Themida-Unpacker / x64dbg ScyllaHide (best-effort; virtualization can't be undone)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
