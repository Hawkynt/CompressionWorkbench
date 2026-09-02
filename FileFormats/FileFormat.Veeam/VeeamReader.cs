#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Veeam;

/// <summary>
/// R/O metadata reader for Veeam Backup &amp; Replication container files
/// (<c>.vbk</c> full backup, <c>.vib</c> incremental backup, <c>.vrb</c>
/// reverse incremental backup).
///
/// <para>
/// Veeam Backup &amp; Replication is an enterprise backup product for VMware /
/// Hyper-V / physical Windows / Linux. Its on-disk container is proprietary,
/// chunked, and CBT-aware (Changed Block Tracking): a backup chain is the
/// combination of one <c>.vbk</c> (full image) plus a sequence of
/// <c>.vib</c> (forward incrementals) or <c>.vrb</c> (reverse incrementals)
/// against an external <c>.vbm</c> metadata index. None of the binary
/// chunk-and-block layer is publicly specified, so the reader does NOT
/// attempt to walk it.
/// </para>
///
/// <para>
/// <b>What this reader DOES extract.</b> Synacktiv's two-part
/// reverse-engineering write-up
/// (<see href="https://www.synacktiv.com/en/publications/using-veeam-metadata-for-efficient-extraction-of-backup-artefacts-13">part 1</see>,
/// <see href="https://www.synacktiv.com/en/publications/using-veeam-metadata-for-efficient-extraction-of-backup-artefacts-23">part 2</see>),
/// pinned by their open-source
/// <see href="https://github.com/synacktiv/veeam-velociraptor">Velociraptor
/// artifact pack</see>, documents an <b>unencrypted plaintext XML island</b>
/// emitted near the trailing edge of each Storage file:
/// <c>&lt;OibSummary&gt; … &lt;/OibSummary&gt;</c>. That island carries
/// the backup-job name, restore-point number, creation time (local + UTC),
/// source/target host names, the storage's partial path, the OIB display
/// name (typically the VM name), object id, the <c>PrevFileName</c> chain
/// link, the backup version field, and a list of restorable disk files
/// with their declared sizes and platform-detail attributes. The reader
/// uses the same "last occurrence" rule as Velociraptor's
/// <c>StartOffsetRule</c> YARA — earlier inline copies may appear inside
/// compressed metadata banks, but the authoritative trailer is the LAST
/// match in the file.
/// </para>
///
/// <para>
/// <b>What this reader CANNOT extract — and why disk content stays Stage 0.</b>
/// </para>
/// <list type="number">
///   <item><b>CBT chain replay is structural.</b> A single <c>.vib</c>/<c>.vrb</c>
///         only carries the delta against its predecessor; reconstructing a
///         restorable image requires walking the full chain via the
///         <c>.vbm</c> metadata index, which lives in a sibling file.</item>
///   <item><b>The dedup store is external.</b> Block bodies are dehydrated
///         against a job-scoped block pool that lives outside the container
///         file — referenced blocks resolve to nothing without the pool.</item>
///   <item><b>AES-256 gates every block when encryption is enabled.</b>
///         Without the Enterprise-Manager-wrapped chain key, the bodies are
///         unrecoverable. The OibSummary XML itself is only emitted in
///         plain text for <i>unencrypted</i> jobs; encrypted backups
///         degrade cleanly to Stage 0 detection-only.</item>
/// </list>
/// </summary>
public sealed class VeeamReader : IDisposable {

  /// <summary>
  /// ASCII <c>VEEAM</c> tag (5 bytes) scanned for within the file's leading
  /// window. Veeam does not document the offset; observed values span the
  /// first 4 KiB depending on writer version and CBT chain role.
  /// </summary>
  public static readonly byte[] VeeamTag = "VEEAM"u8.ToArray();

  /// <summary>Size of the leading window scanned for the <c>VEEAM</c> tag.</summary>
  public const int ScanWindow = 4096;

  private readonly byte[] _data;
  private readonly List<VeeamEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<VeeamEntry> Entries => _entries;
  /// <summary>
  /// Gets or sets the magic offset.
  /// </summary>
public int MagicOffset { get; private set; } = -1;
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
public bool ValidHeader { get; private set; }
  /// <summary>
  /// Gets or sets the file type.
  /// </summary>
public VeeamFileType FileType { get; private set; } = VeeamFileType.Unknown;
  /// <summary>
  /// Gets or sets the trailing word.
  /// </summary>
public uint TrailingWord { get; private set; }

  /// <summary>
  /// Parsed trailing <c>&lt;OibSummary&gt;</c> XML metadata island, when
  /// present and decodable. <c>null</c> for encrypted backups, pre-trailer
  /// writer versions, or truncated containers — Stage 0 detection remains
  /// the safety net in those cases.
  /// </summary>
  public OibSummary? OibSummary { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="VeeamReader"/>.
  /// </summary>
public VeeamReader(Stream stream, VeeamFileType fileTypeHint = VeeamFileType.Unknown) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    this.FileType = fileTypeHint;
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("Veeam: file too small to contain a VEEAM tag.");

    var scanLen = Math.Min(_data.Length, ScanWindow);
    var idx = IndexOf(_data.AsSpan(0, scanLen), VeeamTag);
    if (idx < 0)
      throw new InvalidDataException(
        $"Veeam: 'VEEAM' tag not found in leading {ScanWindow} bytes.");

    this.MagicOffset = idx;
    this.ValidHeader = true;

    // Capture the 4 bytes immediately following the tag as a context word
    // for metadata diagnostics (writer version byte + chunk flags in
    // observed containers; not a spec-stable field, so we surface it raw).
    var trailingPos = idx + VeeamTag.Length;
    if (_data.Length >= trailingPos + 4)
      this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(trailingPos, 4));

    // Stage 1: try the trailing OibSummary XML island. Encrypted backups
    // and pre-trailer writer versions return null, and that's fine — the
    // descriptor falls back to magic-only Stage-0 surfacing.
    this.OibSummary = OibSummaryParser.TryParse(_data);

    var meta = BuildMetadata();
    _entries.Add(new VeeamEntry {
      Name = "metadata.ini",
      Size = meta.Length,
      IsDirectory = false,
      Offset = 0,
      Data = meta,
    });

    // Surface the recovered OibSummary XML as a separate entry too — it's
    // the verbatim Veeam-emitted trailer and is far more useful to a
    // downstream forensics consumer than the .ini summary alone.
    if (this.OibSummary?.RawXml is { Length: > 0 } rawXml) {
      var xmlBytes = Encoding.UTF8.GetBytes(rawXml);
      _entries.Add(new VeeamEntry {
        Name = "OibSummary.xml",
        Size = xmlBytes.Length,
        IsDirectory = false,
        Offset = this.OibSummary.XmlOffset,
        Data = xmlBytes,
      });
    }

    _entries.Add(new VeeamEntry {
      Name = SyntheticPayloadName(),
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });
  }

  private string SyntheticPayloadName() => this.FileType switch {
    VeeamFileType.Full => "veeam-full.vbk.bin",
    VeeamFileType.Incremental => "veeam-incremental.vib.bin",
    VeeamFileType.ReverseIncremental => "veeam-reverse.vrb.bin",
    _ => "veeam-container.bin",
  };

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    var oib = this.OibSummary;
    var stage = oib != null ? 1 : 0;
    var status = oib != null ? "metadata-only" : "detection-only";

    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={status}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"stage={stage}\n");
    bldr.Append("format=Veeam Backup & Replication container\n");
    bldr.Append(CultureInfo.InvariantCulture, $"file_type={this.FileType}\n");
    bldr.Append("extensions=.vbk .vib .vrb\n");
    bldr.Append("file_type_vbk=.vbk = full backup (Veeam Backup Key)\n");
    bldr.Append("file_type_vib=.vib = incremental backup\n");
    bldr.Append("file_type_vrb=.vrb = reverse incremental backup\n");
    bldr.Append("magic_tag=VEEAM\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_offset={this.MagicOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_scan_window={ScanWindow}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word_after_tag=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");

    if (oib != null) {
      bldr.Append("oib_summary_found=true\n");
      bldr.Append(CultureInfo.InvariantCulture, $"oib_summary_offset={oib.XmlOffset}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"oib_summary_length={oib.XmlLength}\n");
      AppendIfPresent(bldr, "job_name", oib.JobName);
      AppendIfPresent(bldr, "policy_name", oib.PolicyName);
      if (oib.BackupTypeCode is { } btc)
        bldr.Append(CultureInfo.InvariantCulture,
          $"backup_type_code={btc} ({BackupTypeName(btc)})\n");
      if (oib.RestorePointTypeCode is { } rptc)
        bldr.Append(CultureInfo.InvariantCulture,
          $"restore_point_type_code={rptc} ({BackupTypeName(rptc)})\n");
      if (oib.EncryptionCode is { } enc)
        bldr.Append(CultureInfo.InvariantCulture,
          $"encryption_code={enc} ({EncryptionName(enc)})\n");
      if (oib.EncryptionStateCode is { } encS)
        bldr.Append(CultureInfo.InvariantCulture,
          $"encryption_state_code={encS} ({EncryptionName(encS)})\n");
      if (oib.RestorePointNumber is { } rpn)
        bldr.Append(CultureInfo.InvariantCulture, $"restore_point_number={rpn}\n");
      AppendIfPresent(bldr, "creation_time", oib.CreationTime);
      AppendIfPresent(bldr, "creation_time_utc", oib.CreationTimeUtc);
      AppendIfPresent(bldr, "storage_partial_path", oib.StoragePartialPath);
      AppendIfPresent(bldr, "oib_display_name", oib.OibDisplayName);
      AppendIfPresent(bldr, "oib_vm_name", oib.OibVmName);
      AppendIfPresent(bldr, "oib_state", oib.OibState);
      AppendIfPresent(bldr, "oib_type", oib.OibType);
      AppendIfPresent(bldr, "oib_algorithm", oib.OibAlgorithm);
      AppendIfPresent(bldr, "oib_health_status", oib.OibHealthStatus);
      AppendIfPresent(bldr, "oib_creation_time_utc", oib.OibCreationTimeUtc);
      AppendIfPresent(bldr, "oib_completion_time_utc", oib.OibCompletionTimeUtc);
      if (oib.OibApproxSize is { } sz0)
        bldr.Append(CultureInfo.InvariantCulture, $"oib_approx_size={sz0}\n");
      if (oib.OibEffectiveMemoryMb is { } mem)
        bldr.Append(CultureInfo.InvariantCulture, $"oib_effective_memory_mb={mem}\n");
      AppendIfPresent(bldr, "oib_has_index", oib.OibHasIndex);
      AppendIfPresent(bldr, "oib_has_exchange", oib.OibHasExchange);
      AppendIfPresent(bldr, "oib_has_sharepoint", oib.OibHasSharePoint);
      AppendIfPresent(bldr, "oib_has_sql", oib.OibHasSql);
      AppendIfPresent(bldr, "oib_has_ad", oib.OibHasAd);
      AppendIfPresent(bldr, "oib_has_oracle", oib.OibHasOracle);
      AppendIfPresent(bldr, "oib_has_postgresql", oib.OibHasPostgreSql);
      AppendIfPresent(bldr, "oib_has_veeam_archiver", oib.OibHasVeeamArchiver);
      AppendIfPresent(bldr, "oib_is_corrupted", oib.OibIsCorrupted);
      AppendIfPresent(bldr, "oib_is_recheck_corrupted", oib.OibIsRecheckCorrupted);
      AppendIfPresent(bldr, "oib_is_consistent", oib.OibIsConsistent);
      AppendIfPresent(bldr, "oib_is_partial_active_full", oib.OibIsPartialActiveFull);
      AppendIfPresent(bldr, "oib_product_version", oib.OibProductVersion);
      AppendIfPresent(bldr, "oib_product_version_flags", oib.OibProductVersionFlags);
      AppendIfPresent(bldr, "oib_product_is_rental_license", oib.OibProductIsRentalLicense);
      if (oib.OibAuxDataRaw is { Length: > 0 })
        bldr.Append(CultureInfo.InvariantCulture,
          $"oib_aux_data_length={oib.OibAuxDataRaw.Length}\n");
      AppendIfPresent(bldr, "object_name", oib.ObjectName);
      AppendIfPresent(bldr, "object_id", oib.ObjectId);
      AppendIfPresent(bldr, "object_id_new", oib.ObjectIdNew);
      AppendIfPresent(bldr, "object_vi_type", oib.ObjectViType);
      AppendIfPresent(bldr, "source_host_name", oib.SourceHostName);
      AppendIfPresent(bldr, "source_host_instance_id", oib.SourceHostInstanceId);
      AppendIfPresent(bldr, "target_host_name", oib.TargetHostName);
      AppendIfPresent(bldr, "prev_file_name", oib.PrevFileName);
      AppendIfPresent(bldr, "backup_version", oib.BackupVersion);
      bldr.Append(CultureInfo.InvariantCulture, $"oib_file_count={oib.OibFiles.Count}\n");
      for (var i = 0; i < oib.OibFiles.Count; ++i) {
        var f = oib.OibFiles[i];
        AppendIfPresent(bldr, $"oib_file_{i}_name", f.Name);
        if (f.Size is { } sz)
          bldr.Append(CultureInfo.InvariantCulture, $"oib_file_{i}_size={sz}\n");
      }
      bldr.Append("treatment=Stage 1 - OibSummary trailer metadata extracted; disk content remains Stage 0 (CBT chain + dedup pool + AES gate)\n");
    } else {
      bldr.Append("oib_summary_found=false\n");
      bldr.Append("oib_summary_reason=no_plaintext_trailer - file is encrypted, pre-trailer writer version, or truncated.\n");
      bldr.Append("treatment=Stage 0 confirmed (proprietary CBT-aware Veeam container; magic-only detection)\n");
    }

    bldr.Append("ro_promotion=blocked_for_disk_content\n");
    bldr.Append("ro_promotion_reason_1=no_published_spec - Veeam has not published the on-disk container format. ");
    bldr.Append("Synacktiv's pipeline (github.com/synacktiv/veeam-velociraptor) calls Veeam's own Extract.exe via ");
    bldr.Append("Windows.Veeam.Extract.yaml rather than parsing chunks; the YARA rules in ");
    bldr.Append("Windows.Veeam.RestorePoints.BackupFiles cover ONLY the trailing plaintext OibSummary XML island. ");
    bldr.Append("Veeam R&D forum thread t93873 documents only 'header + metadata bank pairs + data, ");
    bldr.Append("variable offsets due to compression' at block-diagram level - no byte layout. ");
    bldr.Append("SosRansomware's 2024 Backup Extractor tool ships proprietary without published spec. ");
    bldr.Append("No public binary-level reverse engineering of VeeamAgent.exe or VeeamDataMover exists.\n");
    bldr.Append("ro_promotion_reason_2=cbt_chain_replay_required - incremental (.vib) and reverse incremental (.vrb) ");
    bldr.Append("files carry only deltas against a predecessor; reconstructing a restorable image requires ");
    bldr.Append("walking the full chain via the .vbm metadata index.\n");
    bldr.Append("ro_promotion_reason_3=deduplication_store_external - block bodies are dehydrated against a ");
    bldr.Append("job-scoped dedup pool that lives outside the file. Default block size is 1 MiB before compression, ");
    bldr.Append("~300-700 KiB after (Veeam best-practice guide).\n");
    bldr.Append("ro_promotion_reason_4=encryption - Veeam jobs may be AES-256 encrypted. Only the password-VERIFICATION ");
    bldr.Append("blob algorithm is publicly documented (PBKDF2-HMAC-SHA1, 10000 iterations, 64-byte salt, AES-256-CBC ");
    bldr.Append("decryption of a 16-byte check blob with last-12-bytes-equal-0x0C padding - hashcat issue #3623); ");
    bldr.Append("the per-block decryption key derivation and the on-disk salt/cipher layout are NOT documented. ");
    bldr.Append("Without the chain key the blocks are unrecoverable even with the verification algorithm.\n");
    bldr.Append("companion_files=.vbm (backup metadata / chain index), repository chain anchor\n");
    bldr.Append("oib_summary_schema_source=Synacktiv Windows.Veeam.RestorePoints.BackupFiles (Velociraptor Artifact Exchange) - ");
    bldr.Append("AttrJobName/AttrPolicyName/AttrEncryptionState/AttrNum/AttrType/AttrDisplayName/AttrVmName/AttrState/AttrAlgorithm/");
    bldr.Append("AttrHealthStatus/AttrCreationTimeUtc/AttrCompletionTimeUtc/AttrApproxSize/AttrAuxData/AttrEffectiveMemoryMb/");
    bldr.Append("AttrHas*/AttrIs*/AttrProductVersion*/AttrHostInstanceId/AttrViType/AttrObjectId\n");

    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private static void AppendIfPresent(StringBuilder bldr, string key, string? value) {
    if (string.IsNullOrEmpty(value)) return;
    bldr.Append(CultureInfo.InvariantCulture, $"{key}={value}\n");
  }

  private static string BackupTypeName(int code) => code switch {
    0 => "Full",
    1 => "Increment",
    _ => "Unknown",
  };

  private static string EncryptionName(int code) => code switch {
    0 => "Unencrypted",
    2 => "Encrypted",
    _ => "Unknown",
  };

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(VeeamEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }

  private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0 || haystack.Length < needle.Length)
      return -1;
    var last = haystack.Length - needle.Length;
    for (var i = 0; i <= last; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match) return i;
    }
    return -1;
  }
}

/// <summary>
/// Veeam Backup &amp; Replication container role within a backup chain.
/// </summary>
public enum VeeamFileType {
  /// <summary>Role unknown — caller did not classify by extension.</summary>
  Unknown = 0,
  /// <summary>Full backup (.vbk — Veeam Backup Key).</summary>
  Full = 1,
  /// <summary>Forward incremental backup (.vib).</summary>
  Incremental = 2,
  /// <summary>Reverse incremental backup (.vrb).</summary>
  ReverseIncremental = 3,
}
