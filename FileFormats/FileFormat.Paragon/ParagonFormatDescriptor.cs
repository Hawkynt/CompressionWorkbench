#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Paragon;

/// <summary>
/// R/O metadata descriptor for Paragon Backup &amp; Recovery (<c>.pbf</c>)
/// sector-image backup files. Surfaces the corrected (TrID-documented)
/// <c>"PImg"</c> magic, a synthetic <c>metadata.ini</c> describing the
/// multi-file companion convention and the format-evolution history, and
/// the raw image bytes; no real entry walk is attempted.
///
/// <para>
/// <b>Promotion outcome: R/O metadata only.</b> The earlier Stage-0 baseline
/// declined any promotion entirely; this revision corrects the detection
/// magic against the public spec and surfaces what little structural
/// information <i>is</i> publicly documented. R/W is still blocked: the
/// byte layout after the 4-byte magic is undocumented, the format is
/// proprietary, vendor restore-only since HDM 16.
/// </para>
///
/// <para>
/// <b>What the deep-RE research established:</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Real magic, not the Stage-0 guess.</b> The TrID file-identifier
///     database catalogues the "Paragon Backup Format image" header as the
///     4-byte ASCII tag <c>"PImg"</c> (hex <c>50 49 6D 67</c>) at offset 0,
///     cross-confirmed by file-extension.net, recoveryutility.com, and
///     datenrettungtool.de. The earlier Stage-0 baseline had used the ASCII
///     tags <c>"PBF"</c> / <c>"PBR1"</c>, which were a guess from the format's
///     display name and never observed in real samples — those have been
///     replaced by the documented <c>"PImg"</c> signature.
///   </description></item>
///   <item><description>
///     <b>Multi-file archive convention is documented.</b> Per Paragon KB
///     article 767 ("Archive Formats"), a complete Paragon backup directory
///     contains: <c>.pbf</c> main image / legacy pre-HDM-11 index;
///     <c>.pfi</c> Paragon Backup Index Data (main index since HDM 11 / late
///     2011, small and used to ship deltas over the network); <c>.pfm</c>
///     Image Descriptor sidecar consumed by Paragon's Image Explorer for
///     fast browsing; and split data chunks <c>.000</c> / <c>.001</c> /
///     <c>.002</c> / ... at the ~4 GB segment boundary.
///   </description></item>
///   <item><description>
///     <b>Format-evolution timeline is documented.</b> PBF was the sole index
///     up to HDM 10 (2009/2010); HDM 11 (late 2011) introduced PFI and
///     demoted PBF to the data file; HDM 14 introduced pVHD (Paragon Virtual
///     Hard Disk) as the new container, with PBF still primary under "Smart
///     Backup"; HDM 15 made pVHD the default, with PBF only via "Legacy
///     Mode"; HDM 16+ removed PBF creation entirely — restore-only.
///   </description></item>
///   <item><description>
///     <b>R/W blockers that remain after research.</b> The byte layout after
///     the 4-byte magic is undocumented in every public source consulted —
///     TrID only catalogues the signature, the Paragon Knowledge Base and the
///     HDM / Backup&amp;Recovery user manuals only describe user-facing
///     operations, and no open-source third-party PBF reader exists. The
///     block index, per-cluster allocation bitmap (sector-based mode),
///     snapshot / incremental chain framing, on-disk compressor identifier,
///     per-block frame header, and per-segment split-archive trailer all
///     remain proprietary. The format is also obsolete for creation since
///     HDM 16.
///   </description></item>
///   <item><description>
///     <b>Deep-RE audit conclusion.</b> Twelve research vectors were pursued
///     past the bare TrID signature on top of the Stage-0 -&gt; R/O baseline:
///     asmodean expimg (false lead, Japanese visual-novel format unrelated
///     to Paragon), Paragon HDM SDK (partitioning only), Paragon-Software-
///     Group + Paragon-Backup-Recovery GitHub orgs (no backup-format code),
///     USPTO patent search (no Paragon-assigned PBF-layout disclosure),
///     EnCase / X-Ways / FTK forensic-suite custom-carver repositories
///     (no Paragon-PBF-specific carver), Russian Habr / Toster.ru threads,
///     paragon284.rssing.com Drive Backup forum mirror (community confirms
///     the conceptual triple {index, metadata, compressed} but no byte-level
///     layout), Gary Kessler / SEARCH file-signatures table (no PBF entry),
///     Kaitai Struct + 010 Editor / Hexinator / Synalize It! / ImHex template
///     libraries (no <c>.ksy</c> / <c>.bt</c> template for PBF), Paragon
///     Scripting Language User Manual (0-9 compression dial, <c>*.pbf</c>
///     exclusion only), and the Paragon ExtFS / NTFS3 / UFSD / APFS-SDK-CE
///     open-source releases (filesystem drivers only, share no structures
///     with PBF). All twelve vectors dead-ended. The audit produced one
///     material correction: legacy PBF is unencrypted; password protection,
///     compression and splitting are pVHD-only per the B&amp;R 17 / HDM 16
///     manuals — the earlier baseline's "optional AES with vendor KDF"
///     blocker was incorrect and is retired. Stage stays at R/O metadata;
///     the audit trail is persisted in <c>metadata.ini</c> as
///     <c>re_audit_*</c> keys so the next maintainer doesn't repeat it.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Sources (all public, all consulted during the R/O promotion):</b>
/// TrID file-identifier database; Paragon KB articles 767 (Archive Formats)
/// and 262 (Backup Types); Paragon Backup &amp; Recovery 17 User Manual;
/// Paragon Hard Disk Manager 16 User Manual; cross-references via
/// file-extension.net, recoveryutility.com, datenrettungtool.de,
/// openthefile.net, fileinfo.com, file.org, solvusoft.com.
/// </para>
/// </summary>
public sealed class ParagonFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Paragon";
  public string DisplayName => "Paragon Backup";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".pbf";
  public IReadOnlyList<string> Extensions => [".pbf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "PImg" (0x50 0x49 0x6D 0x67) - "Paragon Image" tag at offset 0.
    // Documented in the TrID file-identifier database for "Paragon Backup
    // Format image", cross-confirmed by file-extension.net,
    // recoveryutility.com, datenrettungtool.de. This replaces the earlier
    // Stage-0 baseline guess of "PBF" / "PBR1", which were never observed
    // in real samples.
    new("PImg"u8.ToArray(), Offset: 0, Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Paragon Backup & Recovery (.pbf) - R/O metadata + structured header. Proprietary " +
    "Paragon Software backup container produced by Backup & Recovery / Hard Disk Manager / " +
    "Drive Backup. Detection magic 'PImg' (50 49 6D 67) at offset 0 per the TrID file-identifier " +
    "database AND confirmed by Wave-13 binary reverse-engineering of the vendor's " +
    "hdmengine_hdmsdk.dll from HDM 18.12.0.0744. The reader parses the real structured " +
    "+4 Major / +6 FormatVersion fields (writer emits 0x0002 / 0x0003; reader rejects > 3); " +
    "the +0xC / +0x30 / +0xD8 chained-archive identity fields, the +0x26 / +0x27 / +0xE8 flag " +
    "bytes, and the +0x34 string field are documented in metadata.ini for forensic triage. " +
    "Multi-file convention (.pbf main, .pfi index since HDM 11, .pfm Image Explorer sidecar, " +
    ".000/.001/... split chunks at ~4 GB) per Paragon KB article 767. Deep-RE audit summary: " +
    "twelve public-source vectors pursued past the bare TrID signature (asmodean expimg, " +
    "Paragon HDM SDK, Paragon-Software-Group + Paragon-Backup-Recovery GitHub orgs, USPTO patent " +
    "search, EnCase/X-Ways/FTK forensic suites, Russian Habr/Toster forums, paragon284 Drive " +
    "Backup forum mirror, Gary Kessler / SEARCH file-signatures table, Kaitai Struct + 010 " +
    "Editor template libraries, Paragon Scripting Language manual, Paragon ExtFS / NTFS3 / UFSD " +
    "open-source releases); all twelve dead-ended without surfacing chunk-framing detail. " +
    "Wave-13 binary RE then succeeded: the structured header layout, the PBF C++ class " +
    "hierarchy (PbfRWBlock / PbfLink / PbfArc / PbfPart / PbfRW / PbfDataFile / CPbfBitmapIO), " +
    "the segments-of-chunks data layer (PbfDataFile + 'ChunkNumber: %d, ChunkOffSet: 0x%016I64x, " +
    "ChunkSize: %d, ChunkIsCompress: %c' debug strings), the per-chunk zlib / DEFLATE + Adler-32 " +
    "frame model, and the +0xD8 ParentId chain back-pointer (gated by FormatVersion >= 2) all " +
    "recovered from .text + .rdata of the vendor's reader DLL. Material correction the audit " +
    "produced: legacy PBF is unencrypted - password protection, compression and splitting are " +
    "pVHD-only per the Backup & Recovery 17 / HDM 16 manuals (the earlier baseline incorrectly " +
    "listed an AES blocker on legacy PBF). What stays unresolved after Wave 13: the exact " +
    "on-disk offset of the chunk-table inside each segment, the bitmap-chain encoding, the .pfi " +
    "magic bytes (loaded indirectly in the binary), and clean-room byte validation against real " +
    "PBF samples (HDM 16+ is restore-only; the Free Edition only writes pVHD). The full image " +
    "is surfaced as an opaque blob alongside a synthetic metadata.ini that persists the " +
    "re_audit_1..13 trail and the recovered struct_header_* / data_layer_* keys so the next " +
    "promotion pass can extend the parser against a real sample without re-running the binary " +
    "RE. Format is obsolete for creation since HDM 16; restore content with vendor tools.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ParagonReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ParagonReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new ParagonReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"Paragon entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
