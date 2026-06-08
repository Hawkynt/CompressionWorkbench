#pragma warning disable CS1591
namespace FileFormat.Veeam;

/// <summary>
/// Structured view of the <c>&lt;OibSummary&gt;</c> XML metadata block embedded
/// in the trailer of an unencrypted Veeam Backup &amp; Replication Storage
/// file (<c>.vbk</c> / <c>.vib</c> / <c>.vrb</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> The XML envelope is documented in Synacktiv's two-part
/// reverse-engineering write-up "Using Veeam metadata for efficient extraction
/// of Backup artefacts" (parts 1 &amp; 2) and pinned by their Velociraptor
/// artifact <c>Windows.Veeam.RestorePoints.BackupFiles</c>. Both sources
/// observe that an unencrypted Veeam Storage file embeds, near the end of the
/// container, a plain-text XML island bracketed by <c>&lt;OibSummary&gt;</c>
/// and <c>&lt;/OibSummary&gt;</c>. The "last occurrence" rule — pick the
/// trailing match in case the writer left earlier copies inside compressed
/// metadata banks — comes from the Velociraptor YARA rule
/// <c>StartOffsetRule { strings: $start = "&lt;OibSummary&gt;" }</c>.
/// </para>
/// <para>
/// <b>Field map.</b> The canonical attribute names are taken verbatim from
/// Synacktiv's <c>Windows.Veeam.RestorePoints.BackupFiles</c> VQL artifact:
/// <c>Backup/@JobName</c>, <c>Backup/@PolicyName</c>,
/// <c>Backup/@EncryptionState</c>, <c>Point/@Num</c>, <c>Point/@Type</c>,
/// <c>Storage/@PartialPath</c>, <c>OIB/@DisplayName</c>, <c>OIB/@VmName</c>,
/// <c>OIB/@State</c>, <c>OIB/@Type</c>, <c>OIB/@Algorithm</c>,
/// <c>OIB/@HealthStatus</c>, <c>OIB/@CreationTimeUtc</c>,
/// <c>OIB/@CompletionTimeUtc</c>, <c>OIB/@ApproxSize</c>,
/// <c>OIB/@AuxData</c>, <c>OIB/@EffectiveMemoryMb</c>,
/// <c>OIB/@HasIndex</c>, <c>OIB/@HasExchange</c>,
/// <c>OIB/@HasSharePoint</c>, <c>OIB/@HasSql</c>, <c>OIB/@HasAd</c>,
/// <c>OIB/@HasOracle</c>, <c>OIB/@HasPostgreSql</c>,
/// <c>OIB/@HasVeeamArchiver</c>, <c>OIB/@IsCorrupted</c>,
/// <c>OIB/@IsRecheckCorrupted</c>, <c>OIB/@IsConsistent</c>,
/// <c>OIB/@IsPartialActiveFull</c>, <c>OIB/@ProductVersion</c>,
/// <c>OIB/@ProductVersionFlags</c>, <c>OIB/@ProductIsRentalLicense</c>,
/// <c>SourceHost/@Name</c>, <c>SourceHost/@HostInstanceId</c>,
/// <c>Object/@Name</c>, <c>Object/@Id</c>, <c>Object/@ObjectId</c>,
/// <c>Object/@ViType</c>, <c>PrevFileName</c>, <c>BackupVersion</c>,
/// <c>OibFiles/File/@Name</c>, <c>OibFiles/File/@Size</c>. Earlier writer
/// versions may emit a subset; missing attributes surface as <c>null</c>.
/// </para>
/// <para>
/// <b>Honest scope.</b> Every field is best-effort: missing attributes
/// surface as <c>null</c>, the parser never throws on unknown shapes, and
/// the disk content remains Stage 0 because reconstructing a restorable
/// image still requires the full CBT chain (.vbm + every link), the
/// external dedup pool, and the AES-256 chain key. This widened surface
/// adds R/O metadata coverage only — it does NOT push past the chunked
/// compressed block layer, which has no published spec.
/// </para>
/// </remarks>
public sealed class OibSummary {

  /// <summary>Backup job name (from <c>&lt;Backup JobName="..."&gt;</c>).</summary>
  public string? JobName { get; init; }

  /// <summary>
  /// Backup policy name (<c>&lt;Backup PolicyName="..."&gt;</c>) — present
  /// when the backup job participates in a Veeam SureBackup or SOBR policy
  /// (Synacktiv: surfaced as a top-level column in the canonical VQL).
  /// </summary>
  public string? PolicyName { get; init; }

  /// <summary>
  /// Backup type code from <c>&lt;Backup Type="..."&gt;</c>.
  /// Synacktiv pinned <c>0 = Full (.vbk)</c>, <c>1 = Increment (.vib)</c>.
  /// </summary>
  public int? BackupTypeCode { get; init; }

  /// <summary>
  /// Encryption flag from <c>&lt;Backup Encryption="..."&gt;</c>
  /// (older writer versions) — older Veeam shape; see
  /// <see cref="EncryptionStateCode"/> for the canonical
  /// <c>EncryptionState</c> attribute used by the Velociraptor artifact.
  /// Velociraptor's <c>Windows.Veeam.RestorePoints.BackupFiles</c> documents
  /// <c>0 = Unencrypted</c>, <c>2 = Encrypted</c>; the OibSummary block is
  /// only emitted in plain text when the backup is unencrypted, so a non-null
  /// value here is almost always 0.
  /// </summary>
  public int? EncryptionCode { get; init; }

  /// <summary>
  /// Canonical encryption-state code from
  /// <c>&lt;Backup EncryptionState="..."&gt;</c> — the attribute name used
  /// by the Synacktiv Velociraptor artifact
  /// <c>Windows.Veeam.RestorePoints.BackupFiles</c>. Semantics match
  /// <see cref="EncryptionCode"/>: <c>0 = Unencrypted</c>, <c>2 = Encrypted</c>.
  /// Most modern writer versions emit <c>EncryptionState</c>; pre-V12 writers
  /// emit the legacy <see cref="EncryptionCode"/>.
  /// </summary>
  public int? EncryptionStateCode { get; init; }

  /// <summary>Restore-point number (from <c>&lt;Point Num="..."&gt;</c>).</summary>
  public int? RestorePointNumber { get; init; }

  /// <summary>
  /// Restore-point type code (<c>&lt;Point Type="..."&gt;</c>) — Synacktiv
  /// VQL maps <c>0 = Full</c>, <c>1 = Increment</c>, mirroring
  /// <see cref="BackupTypeCode"/>. Distinct attribute, surfaced separately
  /// because real writers sometimes set <c>Point/@Type</c> but not
  /// <c>Backup/@Type</c>.
  /// </summary>
  public int? RestorePointTypeCode { get; init; }

  /// <summary>Restore-point local creation time (<c>&lt;Point CreationTime="..."&gt;</c>) — surfaced as raw string.</summary>
  public string? CreationTime { get; init; }

  /// <summary>Restore-point UTC creation time (<c>&lt;Point CreationTimeUtc="..."&gt;</c>) — surfaced as raw string.</summary>
  public string? CreationTimeUtc { get; init; }

  /// <summary>Storage file partial path (<c>&lt;Storage PartialPath="..."&gt;</c>) — typically the relative path within the backup repository.</summary>
  public string? StoragePartialPath { get; init; }

  /// <summary>Object-in-backup display name (<c>&lt;OIB DisplayName="..."&gt;</c>) — often the VM display name.</summary>
  public string? OibDisplayName { get; init; }

  /// <summary>
  /// OIB virtual-machine name (<c>&lt;OIB VmName="..."&gt;</c>) — the
  /// inventory VM name as known to the source hypervisor. Distinct from
  /// <see cref="OibDisplayName"/> which is the Veeam-managed display name.
  /// </summary>
  public string? OibVmName { get; init; }

  /// <summary>OIB state attribute (<c>&lt;OIB State="..."&gt;</c>) — backup-state enum reported by Veeam.</summary>
  public string? OibState { get; init; }

  /// <summary>OIB type attribute (<c>&lt;OIB Type="..."&gt;</c>) — sub-classification of the backed-up object.</summary>
  public string? OibType { get; init; }

  /// <summary>OIB algorithm attribute (<c>&lt;OIB Algorithm="..."&gt;</c>) — backup-method identifier (forever-forward, reverse-incremental, etc.).</summary>
  public string? OibAlgorithm { get; init; }

  /// <summary>OIB health-status attribute (<c>&lt;OIB HealthStatus="..."&gt;</c>).</summary>
  public string? OibHealthStatus { get; init; }

  /// <summary>
  /// OIB creation time in UTC (<c>&lt;OIB CreationTimeUtc="..."&gt;</c>).
  /// Distinct from <see cref="CreationTimeUtc"/> which is the Restore-Point
  /// creation time on <c>&lt;Point&gt;</c>; modern writers emit both.
  /// </summary>
  public string? OibCreationTimeUtc { get; init; }

  /// <summary>OIB completion time in UTC (<c>&lt;OIB CompletionTimeUtc="..."&gt;</c>).</summary>
  public string? OibCompletionTimeUtc { get; init; }

  /// <summary>
  /// OIB approximate backup size (<c>&lt;OIB ApproxSize="..."&gt;</c>) —
  /// declared in bytes by the writer. Surfaced raw so callers can humanize
  /// independently (Velociraptor formats with its <c>humanize()</c> helper).
  /// </summary>
  public long? OibApproxSize { get; init; }

  /// <summary>OIB temporary memory size in MiB (<c>&lt;OIB EffectiveMemoryMb="..."&gt;</c>) — VM-side metric for the snapshotted memory state.</summary>
  public long? OibEffectiveMemoryMb { get; init; }

  /// <summary>
  /// Raw OIB AuxData attribute value (<c>&lt;OIB AuxData="..."&gt;</c>) —
  /// an XML-in-attribute blob carrying a <c>COibAuxData</c> root with
  /// platform-specific guest details (Hyper-V, Desktop, VMware) per
  /// Synacktiv's VQL <c>parse_xml(file=Metadata.OibSummary.OIB.AttrAuxData)</c>.
  /// Surfaced verbatim because the nested schemas vary by platform and
  /// writer version and a flat dictionary would lose structure.
  /// </summary>
  public string? OibAuxDataRaw { get; init; }

  /// <summary>OIB <c>HasIndex</c> capability flag (<c>"true"</c>/<c>"false"</c> string).</summary>
  public string? OibHasIndex { get; init; }

  /// <summary>OIB <c>HasExchange</c> capability flag.</summary>
  public string? OibHasExchange { get; init; }

  /// <summary>OIB <c>HasSharePoint</c> capability flag.</summary>
  public string? OibHasSharePoint { get; init; }

  /// <summary>OIB <c>HasSql</c> capability flag.</summary>
  public string? OibHasSql { get; init; }

  /// <summary>OIB <c>HasAd</c> (Active Directory) capability flag.</summary>
  public string? OibHasAd { get; init; }

  /// <summary>OIB <c>HasOracle</c> capability flag.</summary>
  public string? OibHasOracle { get; init; }

  /// <summary>OIB <c>HasPostgreSql</c> capability flag.</summary>
  public string? OibHasPostgreSql { get; init; }

  /// <summary>OIB <c>HasVeeamArchiver</c> capability flag.</summary>
  public string? OibHasVeeamArchiver { get; init; }

  /// <summary>OIB <c>IsCorrupted</c> health flag.</summary>
  public string? OibIsCorrupted { get; init; }

  /// <summary>OIB <c>IsRecheckCorrupted</c> health flag — set when a re-check pass disagreed with the original CRC.</summary>
  public string? OibIsRecheckCorrupted { get; init; }

  /// <summary>OIB <c>IsConsistent</c> health flag.</summary>
  public string? OibIsConsistent { get; init; }

  /// <summary>OIB <c>IsPartialActiveFull</c> flag.</summary>
  public string? OibIsPartialActiveFull { get; init; }

  /// <summary>OIB <c>ProductVersion</c> — Veeam Backup &amp; Replication version string that wrote this restore point.</summary>
  public string? OibProductVersion { get; init; }

  /// <summary>OIB <c>ProductVersionFlags</c> attribute.</summary>
  public string? OibProductVersionFlags { get; init; }

  /// <summary>OIB <c>ProductIsRentalLicense</c> flag.</summary>
  public string? OibProductIsRentalLicense { get; init; }

  /// <summary>Backed-up object's <c>Name</c> attribute (<c>&lt;Object Name="..."&gt;</c>).</summary>
  public string? ObjectName { get; init; }

  /// <summary>
  /// Backed-up object's legacy <c>Id</c> attribute (<c>&lt;Object Id="..."&gt;</c>).
  /// Modern writers emit <see cref="ObjectIdNew"/> (<c>ObjectId</c>) instead;
  /// the parser surfaces whichever is present.
  /// </summary>
  public string? ObjectId { get; init; }

  /// <summary>
  /// Backed-up object's canonical <c>ObjectId</c> attribute
  /// (<c>&lt;Object ObjectId="..."&gt;</c>) — the attribute name used by
  /// the Synacktiv Velociraptor artifact's <c>AttrObjectId</c> mapping.
  /// </summary>
  public string? ObjectIdNew { get; init; }

  /// <summary>
  /// Backed-up object's <c>ViType</c> attribute (<c>&lt;Object ViType="..."&gt;</c>)
  /// — virtual-infrastructure type tag (e.g. <c>VMware</c>, <c>HyperV</c>).
  /// Maps to Velociraptor's <c>Metadata.OibSummary.Object.AttrViType</c>;
  /// the VQL falls back to literal "Physical machine" when absent.
  /// </summary>
  public string? ObjectViType { get; init; }

  /// <summary>Source host name (<c>&lt;SourceHost Name="..."&gt;</c>) — host that manages the backed-up object.</summary>
  public string? SourceHostName { get; init; }

  /// <summary>
  /// Source-host instance identifier
  /// (<c>&lt;SourceHost HostInstanceId="..."&gt;</c>) — globally unique tag
  /// for the source host; Velociraptor's
  /// <c>Metadata.OibSummary.SourceHost.AttrHostInstanceId</c>.
  /// </summary>
  public string? SourceHostInstanceId { get; init; }

  /// <summary>Target host name (<c>&lt;TargetHost Name="..."&gt;</c>) — host that receives the backed-up object.</summary>
  public string? TargetHostName { get; init; }

  /// <summary>Previous file in the backup chain (<c>&lt;PrevFileName&gt;...&lt;/PrevFileName&gt;</c>) — path to the predecessor .vbk/.vib/.vrb when this file is part of an incremental chain.</summary>
  public string? PrevFileName { get; init; }

  /// <summary>Backup version field (<c>&lt;BackupVersion&gt;N&lt;/BackupVersion&gt;</c>).</summary>
  public string? BackupVersion { get; init; }

  /// <summary>Extractable files declared under <c>&lt;OibFiles&gt;</c>.</summary>
  public IReadOnlyList<OibFileEntry> OibFiles { get; init; } = [];

  /// <summary>Byte offset in the Storage file where the trailing <c>&lt;OibSummary&gt;</c> open tag was found.</summary>
  public long XmlOffset { get; init; }

  /// <summary>Length in bytes of the embedded XML island (open tag through close tag inclusive).</summary>
  public int XmlLength { get; init; }

  /// <summary>Raw XML island as recovered from the container — useful for diagnostics and round-tripping.</summary>
  public string? RawXml { get; init; }
}

/// <summary>
/// Single <c>&lt;File&gt;</c> entry under <c>&lt;OibFiles&gt;</c> — the Veeam
/// extract utility's view of one restorable virtual disk file (.vmdk, .vhdx,
/// .vhd, raw image, etc.) inside this Storage file.
/// </summary>
public sealed class OibFileEntry {

  /// <summary>File name as declared by the writer (typically a path like <c>vm-name-flat.vmdk</c>).</summary>
  public string? Name { get; init; }

  /// <summary>Declared file size in bytes, when present.</summary>
  public long? Size { get; init; }

  /// <summary>Platform/format details surfaced as a flat attribute dictionary (Hyper-V vs. vSphere vs. agent-backup vary materially).</summary>
  public IReadOnlyDictionary<string, string> PlatformDetails { get; init; } =
    new Dictionary<string, string>(StringComparer.Ordinal);
}
