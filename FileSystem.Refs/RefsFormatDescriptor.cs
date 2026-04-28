#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Refs;

/// <summary>
/// Read-only descriptor for Microsoft ReFS (Resilient File System) volume images.
/// Surfaces the parsed boot sector / FSRS header as a structured metadata bundle
/// plus the raw image. Walking the object table / directory B+trees is explicitly
/// out of scope — that's a multi-week effort and Microsoft's documentation is
/// minimal. Detection alone is the primary win here.
/// </summary>
public sealed class RefsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Refs";
  public string DisplayName => "ReFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".refs";
  public IReadOnlyList<string> Extensions => [".refs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // OEM-ID "ReFS\0\0\0\0" at offset 3 of the boot sector — matches Microsoft's
    // documented and reverse-engineered ReFS volume header. Same slot where NTFS
    // stores "NTFS    ", but the trailing nulls disambiguate.
    new([0x52, 0x65, 0x46, 0x53, 0x00, 0x00, 0x00, 0x00], Offset: 3, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft ReFS volume image — boot sector / FSRS header surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.refs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    RefsVolumeHeader hdr;
    try {
      hdr = RefsVolumeHeader.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.refs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.refs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hdr.Valid)
      entries.Add(new ArchiveEntryInfo(2, "volume_header.bin", hdr.RawBytes.LongLength, hdr.RawBytes.LongLength, "stored", false, false, null));
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

    RefsVolumeHeader hdr;
    try {
      hdr = RefsVolumeHeader.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.refs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.refs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
    if (hdr.Valid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.RawBytes, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(RefsVolumeHeader hdr) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(hdr.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"oem_id={hdr.OemId}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sector_size={hdr.SectorSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"bytes_per_cluster={hdr.BytesPerCluster}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_sectors={hdr.TotalSectors}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_major={hdr.MajorVersion}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_minor={hdr.MinorVersion}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_found={hdr.FsrsFound}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_offset={hdr.FsrsOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_length={hdr.FsrsLength}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_checksum=0x{hdr.FsrsCheckSum:X4}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded read — we only need the boot sector for header inspection. Avoid
  // materialising multi-GB streams when the carver runs us speculatively.
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
