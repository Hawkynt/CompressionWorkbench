#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for VMProtect-protected Win32 executables.
/// VMProtect (~2003+) is a commercial virtualizing protector that re-routes
/// selected bytecode through a per-build virtual machine. The unpacker stub
/// commonly renames sections to <c>".vmp0"</c>, <c>".vmp1"</c>, <c>".vmp2"</c>
/// and almost always embeds the literal <c>"VMProtect"</c> in the build for
/// licensing/runtime calls.
/// </summary>
public sealed class VmProtectFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "VmProtect";
  public string DisplayName => "VMProtect (Win32 PE)";
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
    "VMProtect Win32 PE virtualizing protector — surfaces the .vmp0/.vmp1/.vmp2 " +
    "section table and the embedded \"VMProtect\" literal. Decompression " +
    "delegated to VMUnprotect / NoVmp / x64dbg (best-effort; virtualized " +
    "bytecode cannot be losslessly recovered).";

  private static ReadOnlySpan<byte> VmProtectLiteral => "VMProtect"u8;

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
      throw new InvalidDataException("VMProtect: not a valid PE.");

    var sections = PackerScanner.GetPeSections(bytes);
    var vmpSection = sections.FirstOrDefault(s =>
      s.Name.StartsWith(".vmp", StringComparison.OrdinalIgnoreCase));

    var vmProtectIdx = bytes.AsSpan().IndexOf(VmProtectLiteral);

    if (string.IsNullOrEmpty(vmpSection.Name) && vmProtectIdx < 0)
      throw new InvalidDataException("VMProtect: neither .vmp* section nor 'VMProtect' literal found.");

    return [
      ("metadata.ini", BuildMetadata(sections, vmpSection.Name, vmProtectIdx)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string vmpSectionName, int vmProtectIdx) {
    var sb = new StringBuilder();
    sb.AppendLine("[vmprotect]");
    sb.Append(CultureInfo.InvariantCulture, $"vmp_section = {(string.IsNullOrEmpty(vmpSectionName) ? "(none)" : vmpSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"vmprotect_literal_offset = {(vmProtectIdx < 0 ? "(not found)" : $"0x{vmProtectIdx:X6}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to VMUnprotect / NoVmp / x64dbg (best-effort; virtualized bytecode cannot be losslessly recovered)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
