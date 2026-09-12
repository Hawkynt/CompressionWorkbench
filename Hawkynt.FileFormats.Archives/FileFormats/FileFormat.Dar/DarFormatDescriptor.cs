#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dar;

/// <summary>
/// DAR (Disk ARchive) slice — the libdar on-disk container. A slice begins with a
/// slice header carrying the magic number <c>0x00 0x00 0x00 0x7E</c> (libdar's
/// <c>SAUV_MAGIC_NUMBER</c>, stored big-endian) followed by the archive's internal
/// label and a one-byte format/version flag. The terminal slice ends with a
/// catalogue (the file tree) plus a trailing terminator that records the catalogue's
/// start offset, letting a reader seek directly to the file listing.
///
/// <para>Honest scope: this descriptor surfaces a verbatim <c>FULL.dar</c>, a
/// <c>metadata.ini</c> (slice magic validity, detected archive label, header flag,
/// and — when locatable — the catalogue region offset/length read from the trailing
/// terminator) and, when the catalogue region can be bounded, a structural
/// <c>catalogue.bin</c> entry covering it. Full per-member enumeration requires
/// decoding libdar's compressed catalogue tree and is deferred (documented via
/// <c>member_enumeration=deferred</c> in the metadata). Detection is extension-driven
/// (<c>.dar</c>) because the slice magic is too weak to claim generic files.
/// Read-only; malformed input degrades to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://dar.linux.free.fr</c> — official DAR site — libdar archive-structure notes</description></item>
///   <item><description><c>https://github.com/Edrusb/DAR</c> — canonical DAR/libdar source</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Dar_(disk_archiver)</c> — background</description></item>
/// </list>
/// </summary>
public sealed class DarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dar";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Disk ARchive (DAR)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dar";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dar"];
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
    "Disk ARchive (DAR / libdar) slice: slice header (magic 0x0000007E) + label + format flag, " +
    "terminal slice ends with a catalogue + terminator. Surfaces FULL.dar, metadata.ini and a " +
    "structural catalogue.bin when locatable; full member enumeration is deferred. Read-only.";

  // libdar SAUV_MAGIC_NUMBER, stored big-endian at the start of the first slice.
  private const uint SliceMagic = 0x0000007E;

  private sealed record DarModel(
    bool MagicOk,
    uint Magic,
    byte FormatFlag,
    string? Label,
    long CatalogueOffset,
    long CatalogueLength,
    bool Partial);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    var model = Parse(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.dar", data.Length, data.Length, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    };
    if (model.CatalogueOffset > 0 && model.CatalogueLength > 0)
      entries.Add(new ArchiveEntryInfo(2, "catalogue.bin", model.CatalogueLength, model.CatalogueLength, "Stored", false, false, null, Kind: "Track"));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.dar"))
      WriteFile(outputDir, "FULL.dar", data);

    var model = Parse(data);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(model)));

    if (model.CatalogueOffset > 0 && model.CatalogueLength > 0 &&
        model.CatalogueOffset + model.CatalogueLength <= data.Length && Wants(files, "catalogue.bin")) {
      var slab = new byte[model.CatalogueLength];
      Array.Copy(data, model.CatalogueOffset, slab, 0, model.CatalogueLength);
      WriteFile(outputDir, "catalogue.bin", slab);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static DarModel Parse(byte[] data) {
    try {
      if (data.Length < 8)
        return new DarModel(false, 0, 0, null, 0, 0, true);

      var magic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
      var magicOk = magic == SliceMagic;

      // After the magic, libdar writes the internal label (a fixed-width data label,
      // typically 10 bytes) followed by a one-byte format/version flag. We read the
      // label as a best-effort printable token and the flag as the byte after it.
      var label = ExtractLabel(data, 4, 10);
      var formatFlag = data.Length > 14 ? data[14] : (byte)0;

      // The terminal slice ends with a terminator that stores the catalogue start
      // offset. libdar encodes this offset as a variable-length big-endian "infinint"
      // near the end of the file. We probe the trailing bytes for a plausible offset
      // (a value strictly inside the file that points past the header) and treat the
      // region from there to just before the terminator as the catalogue.
      var (catOffset, catLength) = LocateCatalogue(data);

      var partial = !magicOk || catOffset == 0;
      return new DarModel(magicOk, magic, formatFlag, label, catOffset, catLength, partial);
    } catch {
      return new DarModel(false, 0, 0, null, 0, 0, true);
    }
  }

  private static string? ExtractLabel(byte[] data, int offset, int maxLen) {
    if (offset >= data.Length) return null;
    var end = Math.Min(data.Length, offset + maxLen);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) {
      var b = data[i];
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
      else if (sb.Length > 0) break;
    }
    var s = sb.ToString().Trim();
    return s.Length >= 2 ? s : null;
  }

  // Best-effort catalogue locator. libdar's trailing terminator stores the catalogue
  // start offset as a big-endian variable-length integer; we scan the last bytes for
  // a u64/u32 value that points to a sane in-file position and bound the catalogue as
  // [offset, terminatorStart). When nothing plausible is found we return (0,0) and the
  // metadata reports member_enumeration=deferred with no catalogue region.
  private static (long Offset, long Length) LocateCatalogue(byte[] data) {
    if (data.Length < 32) return (0, 0);
    var tail = Math.Min(64, data.Length);
    var span = data.AsSpan(data.Length - tail, tail);
    // Try big-endian u64 candidates within the trailing window.
    for (var i = 0; i + 8 <= span.Length; ++i) {
      var candidate = (long)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(i, 8));
      if (IsPlausibleCatalogueOffset(candidate, data.Length)) {
        var terminatorStart = data.Length - tail + i;
        var len = terminatorStart - candidate;
        if (len > 0) return (candidate, len);
      }
    }
    // Fall back to big-endian u32 candidates.
    for (var i = 0; i + 4 <= span.Length; ++i) {
      var candidate = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i, 4));
      if (IsPlausibleCatalogueOffset(candidate, data.Length)) {
        var terminatorStart = data.Length - tail + i;
        var len = terminatorStart - candidate;
        if (len > 0) return (candidate, len);
      }
    }
    return (0, 0);
  }

  private static bool IsPlausibleCatalogueOffset(long offset, long fileLength)
    => offset >= 16 && offset < fileLength - 8;

  private static string BuildMetadataIni(DarModel m) {
    var sb = new StringBuilder();
    sb.Append("[Dar]\n");
    sb.Append(CultureInfo.InvariantCulture, $"slice_magic_ok={(m.MagicOk ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"magic=0x{m.Magic:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"format_flag={m.FormatFlag}\n");
    if (m.Label != null)
      sb.Append(CultureInfo.InvariantCulture, $"label={m.Label}\n");
    sb.Append(CultureInfo.InvariantCulture, $"catalogue_offset={m.CatalogueOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"catalogue_length={m.CatalogueLength}\n");
    sb.Append("member_enumeration=deferred\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(m.MagicOk && !m.Partial ? "ok" : "partial")}\n");
    return sb.ToString();
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
