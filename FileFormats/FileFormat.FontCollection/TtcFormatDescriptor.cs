#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a TrueType Collection (.ttc) as an archive with:
/// <list type="bullet">
///   <item><description><c>FULL.ttc</c> — verbatim original collection</description></item>
///   <item><description><c>metadata.ini</c> — per-font glyph emission stats</description></item>
///   <item><description><c>fonts/&lt;i&gt;_&lt;name&gt;.{ttf,otf}</c> — sliced standalone member fonts</description></item>
///   <item><description><c>glyphs/&lt;i&gt;_&lt;name&gt;/U+XXXX.svg</c> — per-glyph SVG outlines (TrueType only)</description></item>
/// </list>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/typography/opentype/spec/</c> — OpenType specification — defines the 'ttcf' TrueType Collection header</description></item>
///   <item><description><c>https://developer.apple.com/fonts/TrueType-Reference-Manual/</c> — Apple TrueType Reference Manual — TrueType Collections and glyf outlines</description></item>
/// </list>
/// </summary>
public sealed class TtcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ttc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "TTC (TrueType collection)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ttc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ttc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ttcf"u8.ToArray(), Confidence: 0.95),
  ];
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
public string Description => "TrueType Collection; FULL + per-member fonts + per-glyph SVG.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(Read(stream), fullName: "FULL.ttc")
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.EntryName,
        OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false,
        LastModified: null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildEntries(Read(stream), fullName: "FULL.ttc")) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(entry.EntryName, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.EntryName, entry.Bytes);
    }
  }

  /// <summary>
  /// WORM creation: bundles one or more standalone TTF/OTF inputs into a TTC v1
  /// collection. Inputs must already be valid SFNT fonts (first 4 bytes match a
  /// known sfnt version); the writer rejects anything else so the produced TTC
  /// always describes real fonts. The reader's synthetic FULL.ttc, metadata.ini,
  /// and any per-glyph SVG outputs are filtered so a list-then-create round-trip
  /// recreates the original collection from the per-member font slices.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var fonts = new List<byte[]>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var leaf = Path.GetFileName(i.ArchiveName);
      // Skip reader-emitted synthetic entries.
      if (string.Equals(leaf, "FULL.ttc", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(leaf, "FULL.otc", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(leaf, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      // Skip per-glyph SVG entries (anything under glyphs/ subdir).
      var normalised = i.ArchiveName.Replace('\\', '/');
      if (normalised.StartsWith("glyphs/", StringComparison.OrdinalIgnoreCase)) continue;
      if (normalised.Contains("/glyphs/", StringComparison.OrdinalIgnoreCase)) continue;
      // Only accept .ttf / .otf payloads.
      var ext = Path.GetExtension(leaf).ToLowerInvariant();
      if (ext != ".ttf" && ext != ".otf") continue;
      fonts.Add(i.ReadContent());
    }
    if (fonts.Count == 0)
      throw new ArgumentException("TTC: at least one .ttf or .otf input is required.", nameof(inputs));
    TtcWriter.Write(output, fonts);
  }

  /// <summary>
  /// Builds the full entry list for a .ttc/.otc collection (used by both
  /// <see cref="TtcFormatDescriptor"/> and <see cref="OtcFormatDescriptor"/>).
  /// Each member's glyph emission is wrapped in try/catch — a malformed sub-font
  /// degrades to "fonts/N_..." entry only with parse_status=partial in metadata.ini.
  /// </summary>
  internal static IReadOnlyList<FontGlyphSplitter.SvgEntry> BuildEntries(byte[] full, string fullName) {
    var entries = new List<FontGlyphSplitter.SvgEntry>(64) {
      new(fullName, full),
    };

    List<TtcReader.Member> members;
    try {
      members = new TtcReader().Read(full);
    } catch {
      // Malformed collection — surface FULL only + a parse-status hint.
      entries.Add(new FontGlyphSplitter.SvgEntry(
        "metadata.ini",
        Encoding.UTF8.GetBytes("[collection]\nparse_status = partial\n")));
      return entries;
    }

    var metadata = new StringBuilder();
    metadata.Append("[collection]\n");
    metadata.Append("font_count = ").Append(members.Count).Append('\n');

    foreach (var member in members) {
      var fontName = TtfFormatDescriptor.ResolveFontName(member.Data);
      var folderSegment = $"{member.Index}_{FontGlyphSplitter.SanitiseFolderSegment(fontName ?? $"font_{member.Index}")}";

      // Always emit the per-member font slice.
      entries.Add(new FontGlyphSplitter.SvgEntry(
        $"fonts/{folderSegment}{member.Extension}", member.Data));

      // Per-glyph SVGs — wrap so any single member's failure doesn't poison the rest.
      FontGlyphSplitter.Stats stats;
      try {
        var (svgEntries, s) = FontGlyphSplitter.Split(member.Data, folderSegment);
        entries.AddRange(svgEntries);
        stats = s;
      } catch (Exception ex) {
        stats = new FontGlyphSplitter.Stats(
          fontName, 0, 0, 0, 0, 0, false, $"error: {ex.GetType().Name}");
      }

      metadata.Append('\n').Append(
        TtfFormatDescriptor.BuildMetadataIni(stats, fontIndex: member.Index));
    }

    entries.Add(new FontGlyphSplitter.SvgEntry(
      "metadata.ini", Encoding.UTF8.GetBytes(metadata.ToString())));
    return entries;
  }

  private static byte[] Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
