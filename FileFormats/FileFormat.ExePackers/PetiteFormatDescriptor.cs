#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Petite-packed Win32 executables. Petite
/// (Ian Luck, 1997) was a popular PE compressor in the late 90s; the unpacker
/// stub embeds a section name beginning with <c>".petite"</c> and a literal
/// <c>"Petite"</c> ASCII string near the entry point.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.un4seen.com/petite/</c> — official Petite site (Ian Luck / Un4seen Developments)</description></item>
///   <item><description><c>https://github.com/horsicq/Detect-It-Easy</c> — Detect It Easy — maintained packer-detection signature database</description></item>
/// </list>
/// </summary>
public sealed class PetiteFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Petite";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Petite (Win32 PE)";
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
    "Petite (Ian Luck, 1997+) Win32 PE compressor — surfaces section table " +
    "and embedded \"Petite\" copyright. Decompression delegated to the original " +
    "Petite tool.";

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
      throw new InvalidDataException("Petite: not a valid PE.");

    var sections = PackerScanner.GetPeSections(bytes);
    var petiteSection = sections.FirstOrDefault(s => s.Name.StartsWith(".petite", StringComparison.OrdinalIgnoreCase));
    var hasPetiteString = bytes.AsSpan().IndexOf("Petite"u8) >= 0;

    if (string.IsNullOrEmpty(petiteSection.Name) && !hasPetiteString)
      throw new InvalidDataException("Petite: neither .petite section nor 'Petite' literal found.");

    return [
      ("metadata.ini", BuildMetadata(sections, petiteSection.Name, hasPetiteString)),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string petiteSectionName, bool hasPetiteString) {
    var sb = new StringBuilder();
    sb.AppendLine("[petite]");
    sb.Append(CultureInfo.InvariantCulture, $"petite_section = {(string.IsNullOrEmpty(petiteSectionName) ? "(none)" : petiteSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"petite_string_present = {hasPetiteString}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to the Petite tool\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
