#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nwfs;

/// <summary>
/// Read-only descriptor for NWFS386 (Novell NetWare 386 / "Traditional NetWare
/// File System") — used in NetWare 2.x/3.x/4.x and as the SYS: filesystem in
/// 5.x/6.x. NSS (Novell Storage Services) replaced it for new volumes from
/// 1998 but NWFS images still surface in archaeology / migration workflows.
///
/// **HONEST DISCLAIMER**: this is best-effort detection from public
/// reverse-engineering (notably the zhmu/nwfs project). The on-disk format
/// was never released by Novell. We can identify NWFS partitions by the
/// HOTFIX/MIRROR/Volume signatures but cannot validate contents — directory
/// entries, FAT, suballocation, Turbo FAT etc. are out of scope without an
/// authoritative spec.
///
/// Magic: <c>HOTFIX00</c> — 8 ASCII bytes at byte offset <c>0x4000</c> (16384,
/// = sector 32 at 512 B sectors). Confidence 0.85: 8 bytes of ASCII at a
/// fixed offset is high-signal, but because the layout is RE-derived we keep
/// a small margin below the 0.9-0.95 used for spec-stable filesystems.
/// "MIRROR00" and "NetWare Volumes" are detected as corroboration but not
/// used for primary signature matching.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/zhmu/nwfs</c> — primary reverse-engineering project, incl. <c>doc/nwfs386.md</c></description></item>
///   <item><description><c>https://github.com/jeffmerkey/netware-file-system</c> — secondary reference</description></item>
/// </list>
/// </summary>
public sealed class NwfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nwfs";
  public string DisplayName => "NWFS (Novell NetWare 386 Traditional Filesystem)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".nwfs";
  public IReadOnlyList<string> Extensions => [".nwfs", ".nwvol", ".netware"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "HOTFIX00" at byte offset 0x4000 (sector 32 with 512 B sectors). 8 bytes
    // of ASCII at a fixed offset is high-confidence; we rate this 0.85 (not
    // 0.9+) because the layout is derived from public reverse engineering
    // rather than a vendor-published spec.
    new(NwfsHeaders.HotfixMagic, Offset: (int)NwfsHeaders.HotfixOffset, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NWFS (Novell NetWare 386 Traditional Filesystem) — best-effort detection from public RE; contents cannot be validated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.nwfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    NwfsHeaders hdr;
    try {
      hdr = NwfsHeaders.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.nwfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.nwfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hdr.AnyValid)
      entries.Add(new ArchiveEntryInfo(idx++, "volume_header.bin", hdr.HeaderRaw.LongLength, hdr.HeaderRaw.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    NwfsHeaders hdr;
    try {
      hdr = NwfsHeaders.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.nwfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.nwfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr, image.LongLength), files);
    if (hdr.AnyValid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(NwfsHeaders h, long imageSize) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    // Always "partial" — we never claim "ok" for NWFS because contents can't
    // be validated against an authoritative spec, even when the magic bytes
    // are all present.
    b.Append("parse_status=partial\n");
    b.Append("detection_basis=reverse_engineered\n");
    b.Append(ic, $"hotfix_found={h.HotfixFound}\n");
    if (h.HotfixFound)
      b.Append(ic, $"hotfix_offset={h.HotfixFoundOffset}\n");
    b.Append(ic, $"mirror_found={h.MirrorFound}\n");
    if (h.MirrorFound)
      b.Append(ic, $"mirror_offset={h.MirrorFoundOffset}\n");
    b.Append(ic, $"volumes_found={h.VolumesFound}\n");
    if (h.VolumesFound)
      b.Append(ic, $"volumes_offset={h.VolumesFoundOffset}\n");
    var detected = string.Join("+",
      new[] {
        h.HotfixFound ? "HOTFIX00" : null,
        h.MirrorFound ? "MIRROR00" : null,
        h.VolumesFound ? "NetWare Volumes" : null,
      }.Where(s => s != null));
    b.Append(ic, $"detected_magic={(detected.Length > 0 ? detected : "none")}\n");
    if (imageSize >= 0)
      b.Append(ic, $"volume_size_if_visible={imageSize}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. NWFS HOTFIX header lives at offset 0x4000 with the
  // immediately-following MIRROR sector; "NetWare Volumes" lives within the
  // first ~96 KB. 64 KB covers HOTFIX/MIRROR comfortably and lets the
  // free-form scan find Volumes too.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
