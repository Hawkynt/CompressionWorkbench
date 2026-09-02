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
/// **PROVENANCE**: Novell never released the on-disk format. What is read and
/// written here follows the public reverse-engineering of it (notably the
/// zhmu/nwfs project, whose documentation and reader were both checked
/// against). Volumes written by <see cref="NwfsWriter" /> are read back by that
/// project's own <c>transfer</c> tool — directory tree, sizes and file bytes
/// all agreeing — so contents are no longer merely detected.
///
/// Still out of scope: suballocation, Turbo FAT, compression, mirrored
/// partitions, volumes spanning several partitions, and the salvage area.
/// A volume using any of those reads only as far as its plain structures go.
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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Nwfs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "NWFS (Novell NetWare 386 Traditional Filesystem)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".nwfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".nwfs", ".nwvol", ".netware"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "HOTFIX00" at byte offset 0x4000 (sector 32 with 512 B sectors). 8 bytes
    // of ASCII at a fixed offset is high-confidence; we rate this 0.85 (not
    // 0.9+) because the layout is derived from public reverse engineering
    // rather than a vendor-published spec.
    new(NwfsHeaders.HotfixMagic, Offset: (int)NwfsHeaders.HotfixOffset, Confidence: 0.85),
    // The same header on a whole disk rather than an image of the partition
    // alone: a partition starting at sector 32 puts it 0x4000 further on.
    new(NwfsHeaders.HotfixMagic, Offset: 0x8000, Confidence: 0.80),
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
  public string Description =>
    "NWFS (Novell NetWare 386 Traditional Filesystem) — best-effort detection from public RE; contents cannot be validated.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

    // When the headers lead to a volume, the files on it are listed as well.
    var volume = TryReadVolume(stream);
    if (volume != null)
      foreach (var item in volume.List())
        entries.Add(new ArchiveEntryInfo(idx++, item.Path, item.Length, item.Length, "stored",
                                         item.IsDirectory, false, null));

    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

    var volume = TryReadVolume(stream);
    WriteIfMatch(outputDir, "FULL.nwfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr, image.LongLength, volume), files);
    if (hdr.AnyValid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);

    if (volume == null) return;

    foreach (var item in volume.List()) {
      if (item.IsDirectory) continue;
      WriteIfMatch(outputDir, item.Path, volume.Read(item), files);
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(NwfsHeaders h, long imageSize, NwfsReader? volume) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    // "ok" only when the headers led all the way to a volume and its directory
    // was walked. Magic bytes alone say where to look, not that anything is there.
    b.Append(ic, $"parse_status={(volume != null ? "ok" : "partial")}\n");
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

    if (volume != null) {
      var items = volume.List();
      b.Append(ic, $"volume_name={volume.VolumeName}\n");
      b.Append(ic, $"block_size={volume.BlockSize}\n");
      b.Append(ic, $"file_count={items.Count(i => !i.IsDirectory)}\n");
      b.Append(ic, $"directory_count={items.Count(i => i.IsDirectory)}\n");
    }

    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. NWFS HOTFIX header lives at offset 0x4000 with the
  // immediately-following MIRROR sector; "NetWare Volumes" lives within the
  // first ~96 KB. 64 KB covers HOTFIX/MIRROR comfortably and lets the
  // free-form scan find Volumes too.
  private const int HeaderReadCap = 64 * 1024;

  /// <summary>
  /// How much of an image is taken in to read a volume's files. The headers are
  /// read from the first 64 KB and cost nothing; the whole image is only taken
  /// once those headers say there is a NetWare volume here to read.
  /// </summary>
  private const long VolumeReadCap = 512L * 1024 * 1024;

  /// <summary>
  /// The volume in <paramref name="stream" />, or null when there is none or it
  /// cannot be reached — a stream that will not seek back, or an image past the
  /// size worth taking in.
  /// </summary>
  private static NwfsReader? TryReadVolume(Stream stream) {
    if (!stream.CanSeek) return null;

    try {
      if (stream.Length > VolumeReadCap) return null;

      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      return NwfsReader.TryOpen(ms.ToArray());
    } catch {
      return null;
    }
  }

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
