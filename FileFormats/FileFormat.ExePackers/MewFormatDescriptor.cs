#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for MEW-packed Win32 executables. MEW (by
/// Northfox/HCC, early 2000s) is an extremely small Win32 PE compressor. Its
/// unpacker stub renames at least one section so that the name begins with
/// <c>"MEW"</c> or <c>".MEW"</c> (commonly <c>MEW</c>, <c>MEWF</c>,
/// <c>.MEW</c>).
///
/// References:
/// <list type="bullet">
///   <item><description>MEW 11 SE by Northfox — distributed through the RE scene; no official documentation survives</description></item>
///   <item><description><c>https://github.com/horsicq/Detect-It-Easy</c> — Detect It Easy — maintained packer-detection signature database (MEW signatures)</description></item>
/// </list>
/// </summary>
public sealed class MewFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mew";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MEW (Win32 PE)";
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
    "MEW (Northfox) Win32 PE compressor — surfaces section table and the " +
    "MEW-named section. Decompression delegated to generic PE unpackers " +
    "(RL!dePacker, QUnpack).";

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
      throw new InvalidDataException("MEW: not a valid PE.");

    var sections = PackerScanner.GetPeSections(bytes);
    var mewSection = sections.FirstOrDefault(s =>
      s.Name.StartsWith("MEW", StringComparison.OrdinalIgnoreCase) ||
      s.Name.StartsWith(".MEW", StringComparison.OrdinalIgnoreCase));

    if (string.IsNullOrEmpty(mewSection.Name))
      throw new InvalidDataException("MEW: no section name beginning with \"MEW\" or \".MEW\" found.");

    return [
      ("metadata.ini", BuildMetadata(sections, mewSection.Name)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string mewSectionName) {
    var sb = new StringBuilder();
    sb.AppendLine("[mew]");
    sb.Append(CultureInfo.InvariantCulture, $"mew_section = {mewSectionName}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to RL!dePacker / QUnpack\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
