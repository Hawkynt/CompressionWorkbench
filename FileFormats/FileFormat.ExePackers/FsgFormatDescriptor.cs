#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for FSG ("Fast Small Good") packed Win32 executables.
/// FSG (xt by bart/CRC, early 2000s) is a tiny PE compressor that typically
/// emits a single-section binary; the unpacker stub embeds the distinctive
/// 4-byte ASCII magic <c>"FSG!"</c> usually right at or near the PE entry
/// point.
///
/// References:
/// <list type="bullet">
///   <item><description>FSG by bart/xt — distributed through the packer scene; no official documentation survives</description></item>
///   <item><description><c>https://github.com/horsicq/Detect-It-Easy</c> — Detect It Easy — maintained packer-detection signature database (FSG signatures)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Detection requires <see cref="PackerScanner.IsPe"/> AND the literal
/// <c>FSG!</c> within the first 16 KB of the file — a section-name probe is
/// not reliable since FSG often renames or obliterates the section table.
/// </remarks>
public sealed class FsgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Fsg";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "FSG (Win32 PE)";
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
    "FSG (\"Fast Small Good\") Win32 PE compressor — surfaces section table " +
    "and the embedded \"FSG!\" magic. Decompression delegated to the original " +
    "FSG unpacker / generic PE unpackers (e.g. RL!dePacker, QUnpack).";

  /// <summary>FSG signature ASCII bytes "FSG!".</summary>
  private static ReadOnlySpan<byte> FsgMagic => "FSG!"u8;

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
      throw new InvalidDataException("FSG: not a valid PE.");

    var idx = PackerScanner.IndexOfBounded(bytes, FsgMagic, 0x4000);
    if (idx < 0)
      throw new InvalidDataException("FSG: \"FSG!\" magic not found in first 16 KB.");

    var sections = PackerScanner.GetPeSections(bytes);
    return [
      ("metadata.ini", BuildMetadata(sections, idx)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      int signatureOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[fsg]");
    sb.Append(CultureInfo.InvariantCulture, $"signature_offset = 0x{signatureOffset:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to the FSG unpacker (or RL!dePacker / QUnpack)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
