#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.EaseUs;

/// <summary>
/// Read-only metadata descriptor for EaseUS Todo Backup (<c>.pbd</c>)
/// containers. Parses the IMGF header, surfaces the embedded UTF-16LE
/// source path, locates inner zlib substream offsets, and reports the
/// trailer IMGF marker + 0xFF padding count as a synthetic
/// <c>metadata.ini</c>; the raw container is exposed verbatim as
/// <c>easeus-backup.pbd</c>.
///
/// <para>
/// <b>Treatment: R/O chunk-stream.</b> EaseUS Todo Backup is a proprietary
/// closed-source Chinese backup product from CHENGDU Yiwo Tech
/// Development. The vendor has never published the <c>.pbd</c> on-disk
/// specification; community reverse-engineering (R-Studio custom file
/// type, hex-editor walks on tenforums / xyplorer, binwalk scans on
/// Rune-Server thread 694189) has nailed down the IMGF / FIMG header,
/// the UTF-16LE source-path field, the zlib-substream layout, and the
/// IMGF + 0xFF trailer convention. The reader walks every
/// <c>0x78 {0x01|0x9C|0xDA}</c> candidate zlib substream header in the
/// body, runs a real trial inflate against each one (using
/// <see cref="System.IO.Compression.ZLibStream"/>), and surfaces every
/// confirmed substream as a forensic entry stamped with its offset and
/// compressed / decompressed sizes. The block-allocation table that maps
/// logical sectors back to compressed chunks, the AES-256 key envelope,
/// and the parent-chain backup-job index remain undocumented and gate
/// sector reconstruction — that promotion stays Stage-0 indefinitely.
/// Chunk-stream surfacing is the honest promotion ceiling here. The
/// synthetic metadata.ini pins the upgrade blockers so a future edit
/// can't silently advertise more capability.
/// </para>
///
/// <para>
/// Magic recognised: ASCII <c>"IMGF"</c> (49 4D 47 46) at offset 0 —
/// primary, ~85% of real-world files; ASCII <c>"FIMG"</c> (46 49 4D 47)
/// — byte-reversed variant, ~15% of files (older / OEM builds).
/// R-Studio's 12-byte forensic-carving signature
/// <c>49 4D 47 46 2C 05 00 00 00 00 02 00</c> is detected and reported
/// when present.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.easeus.com</c> — vendor — the .pbd container is proprietary and unpublished</description></item>
///   <item><description>IMGF header / zlib-substream layout recovered by community reverse engineering and binary RE of TBImageExplorer.exe (see remarks above); no public spec exists</description></item>
/// </list>
/// </summary>
public sealed class EaseUsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "EaseUsPbd";
  public string DisplayName => "EaseUS Todo Backup (.pbd)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".pbd";
  public IReadOnlyList<string> Extensions => [".pbd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "IMGF" (49 4D 47 46) — universal Todo Backup container marker at offset 0
    // (~85% of real-world .pbd files per R-Studio + tenforums RE).
    new("IMGF"u8.ToArray(), Offset: 0, Confidence: 0.90),
    // "FIMG" (46 49 4D 47) — byte-reversed variant in ~15% of files
    // (older / OEM builds; documented on xyplorer).
    new("FIMG"u8.ToArray(), Offset: 0, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "EaseUS Todo Backup (.pbd) — R/O metadata via IMGF-header parse + R/O chunk-stream via per-zlib " +
    "trial inflate + binary-reverse-engineered container shape (0x4E8-byte header block + 0xC0-byte " +
    "trailer block pinned by binary RE of TBImageExplorer.exe — CImgFile::CheckHeader at file_off " +
    "0x000CE170 issues ReadFile(buf, 0x4E8) at offset 0 and SetFilePointerEx(EOF-0xC0); ReadFile(buf, " +
    "0xC0) at the tail, then verifies buf[0xBC..0xC0] == 'IMGF') — proprietary closed-source Chinese " +
    "backup container (CHENGDU Yiwo Tech Development); no public on-disk spec, no open-source " +
    "reader, vendor-only engine for sector reconstruction. Reader surfaces the IMGF/FIMG magic, " +
    "header + version words, embedded UTF-16LE source path, trailer IMGF + 0xFF padding count, the " +
    "strict-form 0x4E8 / 0xC0 block validations, AND a per-chunk forensic inventory of every " +
    "confirmed zlib substream (offset / compressed length / decompressed length / payload bytes " +
    "within the per-chunk retention cap) plus the in-memory INDX (block-allocation table — 0x18-byte " +
    "entries behind 'INDX' magic at this+0x14D4), VOLM (per-partition record), FDIR / RIND / FLTR " +
    "sub-record magics as a documented-but-not-yet-walked structure pin in EaseUsContainerIndex. " +
    "Chain holds full/incremental/differential snapshots behind the INDX block-allocation table and " +
    "optional AES-256 key envelope; sector reconstruction (offset-to-LBA mapping via the INDX " +
    "entries, parent-chain replay, encrypted-body decryption) requires either the EaseUS engine " +
    "itself or a sample-driven diff of the header-bank zlib sub-streams at file offsets 0x98 and " +
    "0x10F — chunk-stream + container-shape surfacing is the honest promotion ceiling without that " +
    "corpus. A container writer (EaseUsWriter) is implemented: it emits the recovered 0x4E8 header " +
    "block + zlib-substream body (one stream per stored file behind a manifest) + 0xC0 trailer block, " +
    "and its output round-trips byte-identical through this reader. CanCreate is NOT advertised yet — " +
    "writer implemented; pending vendor-restore validation (a human runs the EaseUS app's restore " +
    "against the produced container to confirm the engine reconstructs the original file tree).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new EaseUsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new EaseUsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new EaseUsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"EaseUS PBD entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
