#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OpenVms;

/// <summary>
/// Read-only descriptor for OpenVMS Files-11 (ODS-2 + ODS-5) volume images
/// — the DEC/VMS native FS used on VAX, Alpha, Itanium and (from 2020) x86-64
/// OpenVMS systems. Surfaces the parsed home block as a structured metadata
/// bundle plus the raw image. Walking the index file and per-file headers to
/// produce a real directory tree is multi-week work and out of scope here.
/// </summary>
public sealed class OpenVmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "OpenVms";
  public string DisplayName => "OpenVMS Files-11";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".ods2";
  public IReadOnlyList<string> Extensions => [".ods2", ".ods5", ".vmsdisk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "DECFILE11A " ASCII at offset 0x1E8 (488) inside the home block which itself
    // sits at logical block 1 (offset 512) → absolute file offset 1000 (0x3E8).
    // Confidence raised from 0.7 → 0.85 so the FilesystemCarver's MinConfidence
    // default (0.5) doesn't false-trigger this reader on random buffers — at the
    // larger 11-byte width false-match rate is already negligible, but keeping
    // it firmly above the median scanner threshold means fewer wasted reader
    // invocations during forensic scans of 10 MB+ random/garbage payloads.
    new("DECFILE11A "u8.ToArray(), Offset: 1000, Confidence: 0.85),
    new("DECFILE11B "u8.ToArray(), Offset: 1000, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DEC/VMS Files-11 (ODS-2 / ODS-5) — home block surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.disk", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    OpenVmsHomeBlock hb;
    try {
      hb = OpenVmsHomeBlock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.disk", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.disk", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "home_block.bin", hb.RawBytes.LongLength, hb.RawBytes.LongLength, "stored", false, false, null));
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

    OpenVmsHomeBlock hb;
    try {
      hb = OpenVmsHomeBlock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.disk", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.disk", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hb), files);
    if (hb.Valid)
      WriteIfMatch(outputDir, "home_block.bin", hb.RawBytes, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(OpenVmsHomeBlock hb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(hb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"home_block_offset={hb.HomeBlockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"format_string={hb.FormatString}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_label={hb.VolumeLabel}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"structure_level=0x{hb.StructureLevel:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"structure_name={hb.StructureName}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"cluster_size={hb.ClusterSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"max_files={hb.MaxFiles}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"owner_uic=0x{hb.OwnerUic:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"index_bitmap_lbn={hb.IndexBitmapLbn}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded — Files-11 home block lives at offset 512 of the volume; 64 KB is
  // overkill for header surfacing and avoids materialising multi-GB carver substreams.
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
