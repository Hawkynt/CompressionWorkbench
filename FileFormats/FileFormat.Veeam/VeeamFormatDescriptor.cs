#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Veeam;

/// <summary>
/// Stage-1 R/O metadata descriptor for Veeam Backup &amp; Replication
/// container files (<c>.vbk</c> full backup, <c>.vib</c> incremental backup,
/// <c>.vrb</c> reverse incremental backup). Disk content stays Stage 0 — see
/// the blockers list below — but the trailing <c>&lt;OibSummary&gt;</c>
/// plaintext XML island is now extracted when present, surfacing the
/// backup-job name, restore-point number, creation time, source/target host,
/// object id, <c>PrevFileName</c> chain link and the list of restorable
/// disk files.
///
/// <para>
/// <b>What the descriptor surfaces.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>metadata.ini</c> — magic offset, image size, file role, and (when
/// available) the parsed OibSummary fields per key.
/// </description></item>
/// <item><description>
/// <c>OibSummary.xml</c> — the verbatim plaintext XML island recovered
/// from the trailer (omitted for encrypted containers and pre-trailer
/// writer versions).
/// </description></item>
/// <item><description>
/// <c>veeam-{full,incremental,reverse,container}.bin</c> — the raw container
/// bytes for downstream tools (e.g. Veeam <c>Extract.exe</c>).
/// </description></item>
/// </list>
///
/// <para>
/// <b>Why disk content remains Stage 0.</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>No published spec for the chunk layer.</b> Veeam has never published
/// the on-disk container format. The only public reverse-engineering
/// surface — Synacktiv's two-part 2024 write-up and the matching
/// <see href="https://github.com/synacktiv/veeam-velociraptor">Velociraptor
/// artifact pack</see> — covers ONLY the trailing plaintext OibSummary
/// XML island. The chunked compressed block layer (header → metadata
/// bank pairs → CRC-protected compressed data blocks, per the
/// <see href="https://forums.veeam.com/veeam-backup-replication-f2/vdk-file-format-t93873.html">Veeam
/// R&amp;D forum thread t93873</see>) is documented at the "block diagram"
/// level only and not enough to walk safely.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>CBT chain replay required.</b> Veeam backups are CBT-aware (Changed
/// Block Tracking): a usable restore image is the combination of one
/// <c>.vbk</c> full plus a sequence of <c>.vib</c> forward incrementals or
/// <c>.vrb</c> reverse incrementals, indexed by an external <c>.vbm</c>
/// metadata sidecar. A single <c>.vib</c>/<c>.vrb</c> file carries only the
/// delta against its predecessor; reading it in isolation cannot produce a
/// restorable image.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Deduplication store is external.</b> Block bodies are dehydrated
/// against a job-scoped deduplication pool that lives outside the container
/// file. Without that pool, referenced blocks resolve to nothing.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Encryption gates every block when configured.</b> Veeam jobs may be
/// AES-256 encrypted with keys wrapped by Enterprise Manager. The key
/// derivation has been reverse-engineered (PBKDF2-HMAC-SHA1, 10000
/// iterations, 64-byte salt, AES-256-CBC verification block — see
/// <see href="https://github.com/hashcat/hashcat/issues/3623">hashcat
/// issue #3623</see>), but the chain key itself remains gated by the
/// password / Enterprise Manager. Encrypted containers degrade cleanly
/// to Stage 0 detection-only — the OibSummary trailer is only emitted
/// in plain text for unencrypted jobs.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// Detection uses the ASCII <c>"VEEAM"</c> tag (5 bytes) anchored at offset 0
/// as the registry's fixed-offset magic. Real containers carry the
/// <c>VEEAM</c> tag within the first 4 KiB but at a writer-version-dependent
/// offset; <see cref="VeeamReader"/> scans the leading 4 KiB window
/// to surface the discovered offset in <c>metadata.ini</c>. The fixed
/// offset-0 magic here is therefore a wrapper convention for registry
/// surfacing only; consumers should treat extension-based detection
/// (<c>.vbk</c> / <c>.vib</c> / <c>.vrb</c>) as primary.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/synacktiv/veeam-velociraptor</c> — Synacktiv's 2024 OibSummary-trailer reverse-engineering (Velociraptor artifact pack), the basis of the Stage-1 reader</description></item>
///   <item><description><c>https://forums.veeam.com/veeam-backup-replication-f2/vdk-file-format-t93873.html</c> — Veeam R&amp;D forum thread describing the chunk layer at block-diagram level</description></item>
///   <item><description><c>https://github.com/hashcat/hashcat/issues/3623</c> — reverse-engineered key derivation for encrypted jobs</description></item>
///   <item><description>Veeam Backup &amp; Replication vendor documentation — the container format itself is proprietary and unpublished</description></item>
/// </list>
/// </summary>
public sealed class VeeamFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Veeam";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Veeam Backup & Replication";
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
public string DefaultExtension => ".vbk";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".vbk", ".vib", ".vrb"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Wrapper-convention tag: ASCII "VEEAM" (5 bytes) at offset 0.
    // Note: real Veeam containers carry the VEEAM tag within the first 4 KiB
    // but at a writer-version-dependent offset (the chunk header that
    // precedes the descriptor is not fixed-size). The offset-0 entry here
    // is the registry's fixed-offset magic surface; VeeamReader scans the
    // leading 4 KiB window to find the real offset and reports it in
    // metadata.ini. Low confidence reflects that this is a surface tag,
    // not a guaranteed format anchor — extension-based detection
    // (.vbk / .vib / .vrb) should be treated as primary.
    new("VEEAM"u8.ToArray(), Offset: 0, Confidence: 0.70),
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
    "Veeam Backup & Replication — Stage 1 trailer-metadata R/O for the OibSummary XML island; disk content stays " +
    "Stage 0 (detection-only) because the chunked compressed block layer has no published spec. " +
    "(.vbk full / .vib incremental / .vrb reverse incremental) — proprietary CBT-aware backup container. " +
    "The trailing plaintext <OibSummary>...</OibSummary> XML island (Synacktiv 2024 — github.com/synacktiv/veeam-velociraptor) " +
    "is extracted when present, with the FULL canonical attribute surface mirrored from the Synacktiv " +
    "Windows.Veeam.RestorePoints.BackupFiles Velociraptor artifact: job_name, policy_name, restore_point_number/type, " +
    "backup/encryption-state codes, OIB display/vm/state/type/algorithm/health, OIB CreationTimeUtc + CompletionTimeUtc + ApproxSize, " +
    "application-presence flags (HasIndex/HasExchange/HasSharePoint/HasSql/HasAd/HasOracle/HasPostgreSql/HasVeeamArchiver), " +
    "health flags (IsCorrupted/IsRecheckCorrupted/IsConsistent/IsPartialActiveFull), ProductVersion + ProductVersionFlags + " +
    "ProductIsRentalLicense, OIB EffectiveMemoryMb, OIB AuxData raw XML blob, source/target host + HostInstanceId, " +
    "object name/id/ObjectId/ViType, PrevFileName chain link, BackupVersion, and the OibFiles list. " +
    "Encrypted backups have no plaintext trailer and degrade cleanly to Stage 0. " +
    "Disk-content R/O promotion remains blocked because (a) no published spec exists for the chunked compressed block layer " +
    "past the OibSummary trailer — exhaustive 2026 follow-up research (Veeam R&D forum t93873 documents only 'header + " +
    "metadata bank pairs + data, variable offsets due to compression' at block-diagram level; Synacktiv's pipeline calls " +
    "Veeam's own Extract.exe via Windows.Veeam.Extract.yaml rather than parsing chunks; SosRansomware's 2024 Backup " +
    "Extractor tool ships proprietary without published spec; no public binary-level reverse engineering of VeeamAgent.exe " +
    "or VeeamDataMover exists); (b) CBT chain replay requires the full .vbm metadata index plus every chain link; " +
    "(c) deduplication dehydrates block bodies against an external job-scoped pool (1 MiB default block size before " +
    "compression, ~300-700 KiB after); (d) AES-256 job encryption (PBKDF2-HMAC-SHA1 / 10000 iters / 64-byte salt per " +
    "hashcat issue #3623, but only the password-verification check blob algorithm is documented — the per-block " +
    "decryption key derivation is NOT) gates every block. " +
    "Magic 'VEEAM' tag scanned within the first 4 KiB (writer-version-dependent offset).";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VeeamReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VeeamReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new VeeamReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"Veeam entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
