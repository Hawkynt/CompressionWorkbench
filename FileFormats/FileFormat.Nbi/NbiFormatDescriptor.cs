#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nbi;

/// <summary>
/// Net Boot Image (<c>.nbi</c>) — a network-boot loader container (Etherboot/gPXE
/// and Apple NetBoot lineage) with a 512-byte loader header (magic
/// <c>0x1B031336</c>, flags/header-length, load location, exec address and 16-byte
/// segment descriptors) followed by concatenated segment payloads (kernel, ramdisk,
/// mkext/dmg blobs).
///
/// <para>The public spec is thin, so this descriptor ships honest best-effort
/// support: it surfaces the verbatim <c>FULL.nbi</c>, a <c>metadata.ini</c>
/// (segment table + geometry) and a raw <c>payload.bin</c> covering everything after
/// the header sector, plus per-segment slices when the descriptor table parses
/// cleanly. <c>parse_status</c> is <c>ok</c> only when every declared segment fits;
/// otherwise <c>partial</c>. Read-only; malformed input never throws.</para>
/// </summary>
public sealed class NbiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nbi";
  public string DisplayName => "Net Boot Image (NBI)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nbi";
  public IReadOnlyList<string> Extensions => [".nbi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x36, 0x13, 0x03, 0x1B], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Net Boot Image (NBI): 512-byte loader header (magic 0x1B031336) + segment descriptors + " +
    "concatenated payload. Surfaces FULL.nbi, metadata.ini, payload.bin and per-segment slices. Read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    var r = new NbiReader(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.nbi", data.Length, data.Length, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    };
    var idx = 2;
    if (r.IsValid && r.PayloadLength > 0)
      entries.Add(new ArchiveEntryInfo(idx++, "payload.bin", r.PayloadLength, r.PayloadLength,
        "Stored", false, false, null, Kind: "Track"));
    if (r.IsValid && r.SegmentsComplete)
      for (var i = 0; i < r.Segments.Count; ++i) {
        var seg = r.Segments[i];
        entries.Add(new ArchiveEntryInfo(idx++, SegmentName(i), seg.ImageLength, seg.ImageLength,
          "Stored", false, false, null, Kind: "Track"));
      }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    var r = new NbiReader(data);

    if (Wants(files, "FULL.nbi"))
      WriteFile(outputDir, "FULL.nbi", data);

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadata(r, data.Length)));

    if (r.IsValid && r.PayloadLength > 0 && Wants(files, "payload.bin")) {
      var payload = new byte[r.PayloadLength];
      Array.Copy(data, NbiReader.HeaderSectorSize, payload, 0, payload.Length);
      WriteFile(outputDir, "payload.bin", payload);
    }

    if (r.IsValid && r.SegmentsComplete)
      for (var i = 0; i < r.Segments.Count; ++i) {
        var seg = r.Segments[i];
        var name = SegmentName(i);
        if (!Wants(files, name))
          continue;
        var slice = new byte[seg.ImageLength];
        Array.Copy(data, seg.DataOffset, slice, 0, slice.Length);
        WriteFile(outputDir, name, slice);
      }
  }

  private static string SegmentName(int index)
    => string.Format(CultureInfo.InvariantCulture, "segment_{0:D2}.bin", index);

  private static string BuildMetadata(NbiReader r, long fileLength) {
    var sb = new StringBuilder();
    sb.Append("[Nbi]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(r.IsValid ? 1 : 0)}\n");
    if (!r.IsValid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"flags=0x{r.Flags:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_blocks={r.HeaderBlocks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"load_location=0x{r.Location:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"exec_address=0x{r.ExecAddress:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_length={fileLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_offset={NbiReader.HeaderSectorSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_length={r.PayloadLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"segment_count={r.Segments.Count}\n");
    for (var i = 0; i < r.Segments.Count; ++i) {
      var s = r.Segments[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"segment{i}=load=0x{s.LoadAddress:X8},img_len={s.ImageLength},mem_len={s.MemoryLength},offset={s.DataOffset}\n");
    }
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(r.SegmentsComplete ? "ok" : "partial")}\n");
    return sb.ToString();
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
