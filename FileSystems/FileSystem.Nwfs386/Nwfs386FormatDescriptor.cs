#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nwfs386;

/// <summary>
/// Read-only descriptor for Novell NetWare 386 (NWFS386) raw partition dumps,
/// detected via the "NetW" ASCII prefix at offset 0. DOS partition type
/// <c>0x65</c>. The on-disk format is FAT-like but proprietary; no parser is
/// attempted — the image is surfaced as a single opaque entry with
/// metadata.ini noting the partition-type hint.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.win.tue.nl/~aeb/partitions/partition_types-1.html</c> — partition-type catalogue (0x65 = Novell NetWare)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NetWare_File_System</c> — Wikipedia article</description></item>
///   <item><description>Novell NetWare 386 internal documentation — the on-disk format was never published</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>Distinct from <c>FileSystem.Nwfs</c>, which detects via the
/// <c>"HOTFIX00"</c> magic at byte offset 0x4000 (sector-32-aligned NetWare
/// HOTFIX header). NWFS386 here covers raw NWFS partition dumps that
/// expose the "NetW" four-byte tag at offset 0 instead of the HOTFIX area.</para>
/// </remarks>
public sealed class Nwfs386FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nwfs386";
  public string DisplayName => "NWFS386 (Novell NetWare 386 raw)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".nwfs386";
  public IReadOnlyList<string> Extensions => [".nwfs386", ".nw386"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "NetW" ASCII at offset 0 — proprietary marker present in some NWFS386
    // raw partition dumps. Confidence 0.60 — short ASCII string at fixed
    // offset, not from a published vendor spec.
    new([0x4E, 0x65, 0x74, 0x57], Offset: 0, Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Novell NetWare 386 raw partition — opaque single-entry surface.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.nwfs386", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.nwfs386", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    var ok = image.Length >= 4 && image[0] == 'N' && image[1] == 'e' && image[2] == 't' && image[3] == 'W';
    WriteIfMatch(outputDir, "FULL.nwfs386", image, files);

    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(ok ? "ok" : "partial")}\n");
    bldr.Append("magic_text=NetW\n");
    bldr.Append("dos_partition_type=0x65\n");
    bldr.Append("note=NWFS386 raw partition. Layout proprietary; surfaced as opaque blob.\n");
    WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(bldr.ToString()), files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
