#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dar;

/// <summary>
/// DAR (Disk ARchive) slice. Every slice file of a DAR archive starts with a slice header
/// (see <see cref="DarSliceHeader"/>): big-endian magic 123, a 10-byte internal name shared by all
/// slices of the archive, a last-slice flag and an extension that since archive format 8 is a TLV
/// list carrying the slicing scheme and the data name. Format-8 slices also end in a one-byte
/// trailer repeating the last-slice answer, which is the only place it lives when the header flag
/// is <c>'E'</c> (every slice of a multi-slice archive).
///
/// <para>Honest scope: this descriptor surfaces a verbatim <c>FULL.dar</c> and a
/// <c>metadata.ini</c> describing the slice header and trailer. The archive header, catalogue and
/// member data live in the concatenated slice payloads, possibly compressed and encrypted, and are
/// not decoded (<c>member_enumeration=deferred</c>). Detection is extension-driven (<c>.dar</c>)
/// because a four-byte magic of 123 is too weak to claim generic files. Read-only; malformed input
/// degrades to FULL + partial metadata without throwing.</para>
///
/// <para>Verified against slices written by dar 2.8.6: single-slice, <c>-s</c> and <c>-S</c>/<c>-s</c>
/// sets are checked in under <c>Compression.Tests/Dar/ReferenceVectors</c>.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://darbinding.sourceforge.net/specs/dar3.html</c> — DAR format description (slice header, infinint)</description></item>
///   <item><description><c>https://dar.sourceforge.io/doc/Notes.html</c> — dar internals notes (TLV slice header, slice trailer)</description></item>
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
    "Disk ARchive (DAR / libdar) slice: slice header (magic 123, internal name, last-slice flag, " +
    "TLV slicing scheme and data name) and format-8 slice trailer. Surfaces FULL.dar and " +
    "metadata.ini; member enumeration is deferred. Read-only.";

  // The TLV list is a few dozen bytes; this bounds how much of a slice is read to parse it.
  private const int HeaderReadLimit = 64 * 1024;

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var length = Measure(stream);
    return [
      new(0, "FULL.dar", length, length, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var seekable = stream.CanSeek ? stream : Buffer(stream);
    try {
      if (Wants(files, "FULL.dar")) {
        seekable.Position = 0;
        using var target = CreateEntryFile(outputDir, "FULL.dar");
        seekable.CopyTo(target);
      }
      if (Wants(files, "metadata.ini"))
        WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(ReadHeader(seekable))));
    } finally {
      if (!ReferenceEquals(seekable, stream))
        seekable.Dispose();
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static long Measure(Stream stream) {
    if (stream.CanSeek)
      return stream.Length;
    long total = 0;
    var buffer = new byte[81920];
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
      total += read;
    return total;
  }

  private static MemoryStream Buffer(Stream stream) {
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms;
  }

  private static DarSliceHeader ReadHeader(Stream stream) {
    var length = stream.Length;
    var head = new byte[(int)Math.Min(length, HeaderReadLimit)];
    stream.Position = 0;
    stream.ReadExactly(head);
    var lastByte = -1;
    if (length > 0) {
      stream.Position = length - 1;
      lastByte = stream.ReadByte();
    }
    return DarSliceHeader.Parse(head, lastByte);
  }

  private static string BuildMetadataIni(DarSliceHeader h) {
    var sb = new StringBuilder();
    var inv = CultureInfo.InvariantCulture;
    sb.Append("[Dar]\n");
    sb.Append(inv, $"slice_magic_ok={(h.Magic == DarSliceHeader.SliceMagic ? 1 : 0)}\n");
    sb.Append(inv, $"magic=0x{h.Magic:X8}\n");
    if (h.InternalName.Length == DarSliceHeader.NameLength)
      sb.Append(inv, $"internal_name={Convert.ToHexString(h.InternalName)}\n");
    if (h.Flag != '\0')
      sb.Append(inv, $"header_flag={Printable(h.Flag)}\n");
    if (h.Extension != '\0')
      sb.Append(inv, $"extension_flag={Printable(h.Extension)}\n");
    if (h.Trailer is { } trailer)
      sb.Append(inv, $"trailer_flag={trailer}\n");
    sb.Append(inv, $"last_slice={h.IsLastSlice switch { true => "yes", false => "no", null => "unknown" }}\n");
    if (h.HeaderLength is { } headerLength)
      sb.Append(inv, $"slice_header_length={headerLength}\n");
    if (h.FirstSliceSize is { } firstSliceSize)
      sb.Append(inv, $"first_slice_size={firstSliceSize}\n");
    if (h.SliceSize is { } sliceSize)
      sb.Append(inv, $"slice_size={sliceSize}\n");
    if (h.DataName is { } dataName)
      sb.Append(inv, $"data_name={Convert.ToHexString(dataName)}\n");
    if (h.UnknownTlvTypes.Count > 0)
      sb.Append(inv, $"unknown_tlv_types={string.Join(',', h.UnknownTlvTypes)}\n");
    sb.Append("member_enumeration=deferred\n");
    if (h.Problem is { } problem)
      sb.Append(inv, $"problem={problem}\n");
    sb.Append(inv, $"parse_status={(h.IsValid ? "ok" : "partial")}\n");
    return sb.ToString();
  }

  private static string Printable(char c) => c is >= ' ' and < (char)0x7F ? c.ToString() : $"0x{(byte)c:X2}";
}
