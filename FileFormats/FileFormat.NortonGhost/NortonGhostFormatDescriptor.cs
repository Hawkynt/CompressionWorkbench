#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.NortonGhost;

/// <summary>
/// Format descriptor for Norton Ghost <c>.gho</c> / <c>.ghs</c> disk image
/// files — the Binary Research / Symantec backup format used by Ghost 4–7
/// (DOS-era 1996–2001), Ghost 2003 (v7.5) and the modern Ghost 11.x
/// engines that retained the same on-disk container.
///
/// <para>
/// <b>Treatment:</b> Read-Only (R/O). The descriptor exposes <c>List</c>
/// and <c>Extract</c> across the entire Ghost <c>FE EF</c>-magic family,
/// covering uncompressed (Z0), Fast LZ (Z1, the proprietary Binary
/// Research codec) and High (Z3–Z9, plain zlib DEFLATE) modes. The write
/// path is deferred (<c>CanCreate</c> not set) — the Fast LZ encoder
/// requires byte-perfect parity with Symantec's hash-table state machine
/// to round-trip through Ghost Explorer, which can only be validated
/// against real images that are not present in the repo. Users who want
/// to write <c>.gho</c> files should use the original Symantec Ghost
/// Explorer 2003.789 build (free download on archive.org, see
/// <c>references</c> field in the surfaced <c>metadata.ini</c>).
/// </para>
///
/// <para>
/// <b>RE sources:</b> Nyarime's pure-Go open-source parser
/// (<a href="https://github.com/nyarime/gho">github.com/nyarime/gho</a>),
/// which derived the FE EF header, the <c>0x012F18D8</c> record framing,
/// and the Fast LZ codec from Norton Ghost 11.5.1. Cross-checked against
/// Forensic Focus forum byte-level analysis of legacy Ghost 2003 images
/// and the Archive Team format-wiki Ghost Image entry. Symantec's Ghost
/// Explorer 2003.789 binary (archive.org/details/norton-ghost-explorer-version-2003.789)
/// is the authoritative reference implementation.
/// </para>
/// </summary>
public sealed class NortonGhostFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "NortonGhost";
  public string DisplayName => "Norton Ghost (.gho/.ghs)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gho";
  public IReadOnlyList<string> Extensions => [".gho", ".ghs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // File magic 0xFE 0xEF at offset 0; followed by file-type byte 0x01 (single .gho) or 0x09 (span .ghs).
    new([0xFE, 0xEF, 0x01], Offset: 0, Confidence: 0.85),
    new([0xFE, 0xEF, 0x09], Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Z0 (none)"),
    new("fast", "Z1 (Fast LZ)"),
    new("high", "Z3..Z9 (zlib DEFLATE)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Norton Ghost .gho/.ghs disk image (R/O) — DOS-era Binary Research v4-7 through Symantec " +
    "Ghost 2003/11.x; FE EF file magic + 0x012F18D8 record framing; supports Z0/Z1/Z3-Z9 " +
    "compression via the Nyarime RE port of Ghost 11.5.1 Fast LZ. Write path deferred — " +
    "use Symantec Ghost Explorer 2003.789 (archive.org) to create new images.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = new NortonGhostReader(stream);
    return BuildEntries(reader)
      .Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, e.Method, false, false, null))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = new NortonGhostReader(stream);
    foreach (var e in BuildEntries(reader)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.GetData());
    }
  }

  private sealed record Entry(string Name, long Size, string Method, Func<byte[]> GetData);

  private static IEnumerable<Entry> BuildEntries(NortonGhostReader reader) {
    var img = reader.Image;
    var meta = BuildMetadata(img);
    yield return new Entry("metadata.ini", meta.Length, "stored", () => meta);

    if (img.Track0.Length > 0) {
      var sectorBytes = img.Track0;
      yield return new Entry("track0.bin", sectorBytes.Length, "stored", () => sectorBytes);
      var mbrSize = Math.Min(512, sectorBytes.Length);
      if (mbrSize == 512) {
        var mbrCopy = sectorBytes.AsSpan(0, 512).ToArray();
        yield return new Entry("mbr.bin", 512, "stored", () => mbrCopy);
      }
    }

    foreach (var partition in img.Partitions) {
      var method = MethodName(partition.Compression);
      // We don't know the decompressed size up-front without walking blocks; surface a
      // best-effort size = sum of stored block sizes so List() doesn't lie about content.
      long approxCompressed = 0;
      foreach (var (start, end) in partition.DataSpans) approxCompressed += end - start;
      var name = $"partition_{partition.Index:D2}.img";
      var captured = partition;
      yield return new Entry(name, approxCompressed, method, () => reader.DecompressPartition(captured));
    }
  }

  private static string MethodName(byte compression) => compression switch {
    NortonGhostReader.CompressionNone => "Stored (Z0)",
    NortonGhostReader.CompressionOld => "Old (Z1-)",
    NortonGhostReader.CompressionFast => "Fast LZ (Z1)",
    >= 3 and <= 9 => $"High (Z{compression}, zlib)",
    _ => $"Unknown (Z{compression})",
  };

  private static byte[] BuildMetadata(NortonGhostReader.GhostImage img) {
    var sb = new StringBuilder();
    sb.AppendLine("[norton-ghost]");
    sb.Append("magic = FE EF").AppendLine();
    sb.Append(CultureInfo.InvariantCulture, $"file_type = {(byte)img.Header.Type:X2} ({img.Header.Type})\n");
    sb.Append(CultureInfo.InvariantCulture, $"version_byte = 0x{img.Header.VersionByte:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version_family = {VersionFamily(img.Header.VersionByte)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_id = 0x{img.Header.ImageId:X8}\n");
    if (!string.IsNullOrWhiteSpace(img.Header.Description))
      sb.Append(CultureInfo.InvariantCulture, $"description = {img.Header.Description}\n");

    sb.AppendLine();
    sb.AppendLine("[partitions]");
    sb.Append(CultureInfo.InvariantCulture, $"count = {img.Partitions.Count}\n");
    foreach (var p in img.Partitions) {
      sb.Append(CultureInfo.InvariantCulture,
        $"partition_{p.Index:D2} = compression=Z{p.Compression} spans={p.DataSpans.Count} id=0x{p.Id:X8}\n");
    }

    if (img.Track0.Length >= 512) {
      var mbr = NortonGhostReader.ParseMbr(img.Track0.AsSpan(0, 512));
      if (mbr.Count > 0) {
        sb.AppendLine();
        sb.AppendLine("[mbr]");
        foreach (var entry in mbr) {
          sb.Append(CultureInfo.InvariantCulture,
            $"part = status=0x{entry.Status:X2} type=0x{entry.Type:X2} lba_start={entry.LbaStart} lba_size={entry.LbaSize}\n");
        }
      }
    }

    if (img.Warnings.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[warnings]");
      for (var i = 0; i < img.Warnings.Count; i++)
        sb.Append(CultureInfo.InvariantCulture, $"w{i:D2} = {img.Warnings[i]}\n");
    }

    sb.AppendLine();
    sb.AppendLine("[references]");
    sb.AppendLine("nyarime_gho = https://github.com/nyarime/gho");
    sb.AppendLine("ghost_explorer_2003 = https://archive.org/details/norton-ghost-explorer-version-2003.789");
    sb.AppendLine("archive_team_wiki = http://justsolve.archiveteam.org/wiki/Ghost_Image");
    sb.AppendLine("note = Write path deferred — use Symantec Ghost Explorer 2003.789 (free, archive.org) to create new .gho images.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string VersionFamily(byte versionByte) => versionByte switch {
    0 => "raw/zero (corrupt or pre-release)",
    1 => "legacy Z1- (Binary Research pre-2003)",
    2 => "Ghost 2003 / Fast LZ era (Z1)",
    >= 3 and <= 9 => $"High zlib (Z{versionByte}) — Ghost 2003+ / Ghost 11.x",
    _ => $"unknown (0x{versionByte:X2})",
  };
}
