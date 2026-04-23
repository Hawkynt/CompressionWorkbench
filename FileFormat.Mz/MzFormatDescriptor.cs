#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mz;

/// <summary>
/// Pseudo-archive descriptor for DOS MZ executables. Splits the file into
/// <c>header.bin</c> (16-byte DOS header + relocation table + any remaining
/// header paragraphs), <c>body.bin</c> (the program image per the DOS loader's
/// declared image size), and <c>overlay.bin</c> when the file carries more bytes
/// than the declared image length — a common carrier for installer payloads,
/// appended data, and SCUMM-era game resources.
/// </summary>
/// <remarks>
/// PE/NE/LE/LX executables share the MZ magic; those formats are handled by
/// <c>FileFormat.PeResources</c> and dedicated readers. This descriptor's
/// magic-byte confidence is low so PE beats it when both are candidates — on a
/// purely descriptor-level tie the reader still produces sensible output
/// (header + body = the MZ stub, overlay = the PE image).
/// </remarks>
public sealed class MzFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mz";
  public string DisplayName => "DOS MZ executable";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [".exe", ".com", ".ovl", ".bin"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Very low confidence — 'MZ' is shared with every Windows binary; PE / NE / LE / LX
    // descriptors beat this one when they apply.
    new([(byte)'M', (byte)'Z'], Confidence: 0.08),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DOS MZ executable; exposes header, program image, and trailing overlay bytes " +
    "as separate entries. Detects piggybacked extended executables (PE/NE/LE/LX).";

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

  private static IEnumerable<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var image = MzReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    yield return ("metadata.ini", BuildMetadata(image, ms.Length));
    yield return ("header.bin", image.Header);
    if (image.Body.Length > 0) yield return ("body.bin", image.Body);
    if (image.Overlay.Length > 0) yield return ("overlay.bin", image.Overlay);
  }

  private static byte[] BuildMetadata(MzReader.MzImage i, long fileSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[mz]");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {fileSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_size = {i.Header.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"body_size = {i.Body.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"overlay_size = {i.Overlay.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"blocks_in_file = {i.BlocksInFile}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bytes_in_last_block = {i.BytesInLastBlock}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_paragraphs = {i.HeaderParagraphs}\n");
    sb.Append(CultureInfo.InvariantCulture, $"num_relocations = {i.NumRelocs}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reloc_table_offset = 0x{i.RelocTableOffset:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"min_extra_paragraphs = {i.MinExtraParagraphs}\n");
    sb.Append(CultureInfo.InvariantCulture, $"max_extra_paragraphs = {i.MaxExtraParagraphs}\n");
    sb.Append(CultureInfo.InvariantCulture, $"initial_cs_ip = {i.InitialCs:X4}:{i.InitialIp:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"initial_ss_sp = {i.InitialSs:X4}:{i.InitialSp:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum = 0x{i.Checksum:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"overlay_number = {i.OverlayNumber}\n");
    sb.Append(CultureInfo.InvariantCulture, $"e_lfanew = 0x{i.ExtendedHeaderOffset:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"extended_signature = {(string.IsNullOrEmpty(i.ExtendedSignature) ? "(none - pure MZ)" : i.ExtendedSignature)}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
