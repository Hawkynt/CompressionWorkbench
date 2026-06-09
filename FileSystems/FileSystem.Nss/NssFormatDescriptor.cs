#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nss;

/// <summary>
/// Read-only descriptor for NSS (Novell Storage Services) — the pool-based,
/// object-aware filesystem that replaced NWFS386 from NetWare 5+ onwards
/// and remains the default for Novell / OpenText Open Enterprise Server.
///
/// **HONEST DISCLAIMER**: NSS's on-disk format was never publicly
/// documented by Novell. This descriptor identifies NSS-shaped images by
/// scanning for Novell's embedded ASCII anchors ("NSS Pool", "NSSVolume",
/// "SuperBlk", "Novell", "NetWare") in the first 1 MB of the partition.
/// We can locate the pool descriptor and volume / superblock anchors and
/// surface their byte offsets, but we **cannot** walk the object tree or
/// reconstruct files — the layout (block allocation, "Beast" object
/// records, trustee ACL trees) is proprietary.
///
/// Magic: <c>"NSS Pool"</c> — 8 ASCII bytes detected within the first 1 MB
/// via free-form scan. Confidence 0.70 — distinctive enough to seed a
/// match but lower than well-specified filesystems because (a) the layout
/// is RE'd, not vendor-published; (b) the magic is a brand string that
/// can theoretically appear in non-NSS contexts; (c) we cannot validate
/// the surrounding structure.
///
/// References:
/// <list type="bullet">
///   <item><description>Novell (OpenText) NSS File System Administration Guide — operational docs only</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Novell_Storage_Services</c> — pool/volume/object overview</description></item>
///   <item><description>NetWare 6.5 NSS Storage Management Services documentation</description></item>
/// </list>
/// </summary>
public sealed class NssFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "Nss";
  public string DisplayName => "NSS (Novell Storage Services)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".nss";
  public IReadOnlyList<string> Extensions => [".nss"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "NSS Pool" — 8 ASCII bytes; offset 0 with a free-form-scan tolerance.
    // Confidence 0.70 reflects the RE-derived layout caveat.
    new(NssHeaders.NssPoolMagic, Offset: 0, Confidence: 0.70),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NSS (Novell Storage Services) — best-effort anchor detection from publicly " +
    "available reverse-engineered material; object tree contents cannot be reconstructed. " +
    "WORM emit deferred: NSS's on-disk format was never publicly documented by Novell " +
    "(now OpenText). The 'Beast' object record layout, the per-volume B-tree node format, " +
    "and the trustee ACL tree encoding are not described in any vendor or open-source " +
    "material we have access to. Emitting a pool that NetWare 5+ / OES would recognise " +
    "would require a real instance to validate, which we don't have. Pinned at " +
    "read-only with anchor-detection metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    NssReader r;
    try {
      r = new NssReader(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.nss", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.nss", r.ImageLength, r.ImageLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (r.AnyValid)
      entries.Add(new ArchiveEntryInfo(idx++, "volume_header.bin", r.HeaderRaw.LongLength, r.HeaderRaw.LongLength, "stored", false, false, null));
    foreach (var e in r.Entries)
      entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    NssReader r;
    try {
      r = new NssReader(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(r), files);
    if (r.AnyValid) {
      WriteIfMatch(outputDir, "volume_header.bin", r.HeaderRaw, files);
      foreach (var e in r.Entries) {
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        var data = r.ExtractAnchor(e);
        WriteFile(outputDir, e.Name, data);
      }
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Nss read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Nss read-only — defragmentation requires a writer.");

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(NssReader r) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    // Always "partial" for NSS — we never claim "ok" because the object tree
    // can't be validated against a published spec, even when every anchor matches.
    b.Append("parse_status=partial\n");
    b.Append("detection_basis=reverse_engineered\n");
    b.Append(ic, $"pool_found={r.Headers.PoolFound}\n");
    if (r.Headers.PoolFound)
      b.Append(ic, $"pool_offset={r.Headers.PoolFoundOffset}\n");
    b.Append(ic, $"volume_found={r.Headers.VolumeFound}\n");
    if (r.Headers.VolumeFound)
      b.Append(ic, $"volume_offset={r.Headers.VolumeFoundOffset}\n");
    b.Append(ic, $"superblock_found={r.Headers.SuperblockFound}\n");
    if (r.Headers.SuperblockFound)
      b.Append(ic, $"superblock_offset={r.Headers.SuperblockFoundOffset}\n");
    b.Append(ic, $"novell_brand_found={r.Headers.NovellFound}\n");
    b.Append(ic, $"netware_brand_found={r.Headers.NetWareFound}\n");
    if (r.VolumeName.Length > 0)
      b.Append(ic, $"volume_name={r.VolumeName}\n");
    var detected = string.Join("+",
      new[] {
        r.Headers.PoolFound ? "NSS Pool" : null,
        r.Headers.SuperblockFound ? "SuperBlk" : null,
        r.Headers.VolumeFound ? "NSSVolume" : null,
      }.Where(s => s != null));
    b.Append(ic, $"detected_magic={(detected.Length > 0 ? detected : "none")}\n");
    if (r.ImageLength >= 0)
      b.Append(ic, $"scan_window_size={r.ImageLength}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }
}
