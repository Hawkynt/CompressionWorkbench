#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Paragon;

/// <summary>
/// R/O metadata reader for Paragon Backup &amp; Recovery (<c>.pbf</c>) sector-image
/// backup files produced by Paragon Software's imaging products (Paragon Backup
/// &amp; Recovery, Hard Disk Manager, Drive Backup).
///
/// <para>
/// <b>Detection (real spec):</b> a Paragon backup image begins with the
/// 4-byte ASCII tag <c>"PImg"</c> (Paragon Image), hex <c>50 49 6D 67</c>, at
/// offset 0. This signature is documented in the TrID file-identifier database
/// (Marco Pontello's signature catalogue) as the "Paragon Backup Format image"
/// header, and is confirmed by independent file-extension reference sites
/// (file-extension.net, recoveryutility.com, datenrettungtool.de). The earlier
/// Stage-0 baseline of this project had pinned the ASCII tags <c>"PBF"</c> /
/// <c>"PBR1"</c>; those were a guess from the format's display name, never
/// observed in real samples, and this R/O promotion corrects them.
/// </para>
///
/// <para>
/// <b>Multi-file archive structure:</b> a complete Paragon backup is not a
/// single file. Per Paragon's own Knowledge Base (kb.paragon-software.com
/// article 767, "Archive Formats"), a PBF backup directory typically contains:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>backup.pbf</c> — main image / legacy index file. Up to Hard Disk
///     Manager 10 (2009/2010) this <i>was</i> the index. Starting with HDM 11
///     (late 2011) the role moved to <c>.pfi</c>.
///   </description></item>
///   <item><description>
///     <c>backup.pfi</c> — Paragon Backup Index Data. Post-HDM-11 main index;
///     small (a few megabytes) and keeps meta-information on the corresponding
///     incremental image so a net-stored full archive only has to ship the
///     index over the wire to compute the delta. Can be rebuilt from a healthy
///     <c>.pbf</c> via the vendor's Refresh action.
///   </description></item>
///   <item><description>
///     <c>backup.pfm</c> — Paragon Backup Image Descriptor. Supplementary file
///     consumed by Paragon's Image Explorer for fast in-image navigation
///     without scanning the full <c>.pbf</c>.
///   </description></item>
///   <item><description>
///     <c>backup.000</c>, <c>backup.001</c>, <c>backup.002</c>, ... — split
///     data chunks at the legacy ~4 GB segment boundary used by Paragon's
///     splitter (and by FAT32 destination volumes).
///   </description></item>
/// </list>
///
/// <para>
/// <b>What this reader does:</b> verifies the documented <c>"PImg"</c> magic
/// at offset 0, captures the four bytes immediately following the magic as a
/// diagnostic trailing word, and surfaces a synthetic <c>metadata.ini</c>
/// describing the multi-file companion convention, the format-evolution
/// history (HDM 10 / 11 / 14 / 15 / 16), and the structural blockers that
/// keep us from a real entry walk. The raw image is exposed as the opaque
/// blob <c>paragon-backup.bin</c>.
/// </para>
///
/// <para>
/// <b>What this reader still does not do (R/W blocked):</b> the byte layout
/// after the 4-byte magic is undocumented in every public source consulted —
/// TrID only catalogues the signature, the Paragon Knowledge Base and the
/// HDM / Backup&amp;Recovery user manuals only describe user-facing operations,
/// and no open-source third-party PBF reader exists. The block index,
/// per-cluster allocation bitmap, snapshot / incremental chain framing, and
/// the per-segment split-archive trailer all remain proprietary. The format
/// is also obsolete for <i>creation</i> (HDM 16+ can only restore PBF, not
/// write it; HDM 14 switched the default to pVHD = Paragon Virtual Hard
/// Disk), so vendor tools are the only safe path to extract content.
/// </para>
///
/// <para>
/// <b>Deep-RE audit (research vectors pursued, all dead-ends documented).</b>
/// Twelve distinct angles were chased past the bare TrID signature; all
/// terminated without surfacing chunk-framing detail. Persisted in
/// <c>metadata.ini</c> as <c>re_audit_*</c> keys so the next maintainer
/// doesn't repeat the same searches.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>asmodean "expimg" tool (asmodean.reverse.net/pages/expimg.html).</b>
///     False lead — the "PImg" name there refers to a Japanese visual-novel
///     archive format, no relation to Paragon Software.
///   </description></item>
///   <item><description>
///     <b>Paragon HDM SDK (developers.paragon-software.com/hdm-sdk).</b>
///     Scope is restricted to partitioning operations (resize / move / merge /
///     split / create / format / check); the backup container layout is not
///     documented. The <c>hdmengine</c> / <c>hdmclient</c> / <c>biontdrv</c>
///     headers are partition-management headers, not archive-format headers.
///   </description></item>
///   <item><description>
///     <b>Paragon Software Group GitHub organisation
///     (github.com/Paragon-Software-Group).</b> Contains <c>linux-ntfs3</c>,
///     <c>paragon-lowcode-oss</c>, <c>paragon_portable_stl</c>,
///     <c>paragon_apfs_sdk_ce</c>, <c>paragon_firewall_ce</c>,
///     <c>eucalyptus</c> — all filesystem drivers, runtimes, or unrelated
///     products. No backup-format code.
///   </description></item>
///   <item><description>
///     <b>Paragon-Backup-Recovery GitHub organisation
///     (github.com/Paragon-Backup-Recovery).</b> Profile-only org with a
///     single <c>.github</c> README repository. No backup-format code.
///   </description></item>
///   <item><description>
///     <b>USPTO patent database.</b> No Paragon-Software-Group-assigned patent
///     disclosing the on-disk PBF layout. Disk-image patents in this space
///     mostly belong to Veritas / Symantec / Acronis — not Paragon.
///   </description></item>
///   <item><description>
///     <b>Forensic suite documentation (EnCase / X-Ways / FTK).</b> All three
///     ship generic-carving signatures and custom-type definitions; none
///     documents a Paragon-PBF-specific carver or content-walk recipe.
///   </description></item>
///   <item><description>
///     <b>Russian-language Habr Q&amp;A / Toster.ru threads.</b> Best public
///     summary of the community position: "PBF is closed, Paragon utilities
///     are the only way." No chunk-framing detail.
///   </description></item>
///   <item><description>
///     <b>paragon284.rssing.com Paragon Drive Backup product-line forum
///     mirror.</b> A user troubleshooting an unrestorable PBF describes
///     "the Paragon file directory structure is fine, the index file,
///     metadata and compressed backup files all appear to be ok." Confirms
///     the conceptual triple ({index, metadata, compressed data files}) but
///     does not surface any byte-level layout, offsets, or magic words
///     beyond "PImg".
///   </description></item>
///   <item><description>
///     <b>Gary Kessler / SEARCH file-signatures database
///     (garykessler.net / filesig.search.org).</b> The authoritative forensic
///     master magic database has no PBF entry — confirmed by direct fetch.
///   </description></item>
///   <item><description>
///     <b>Kaitai Struct format library (kaitai-io/kaitai_struct_formats) and
///     010 Editor / Hexinator / Synalize It! / ImHex template repositories.</b>
///     No <c>.ksy</c> or <c>.bt</c> template exists for PBF.
///   </description></item>
///   <item><description>
///     <b>Paragon Scripting Language User Manual
///     (download.paragon-software.com/doc/script_man_.pdf).</b> References
///     <c>*.pbf</c> only as an extension to <i>exclude</i> from file-level
///     backups and confirms the 0-9 compression-level dial. No struct layout.
///   </description></item>
///   <item><description>
///     <b>Paragon ExtFS / NTFS3 / UFSD / APFS-SDK-CE open-source releases.</b>
///     These are filesystem drivers, not backup-archive drivers; they share
///     no data structures with the PBF container.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Material correction the audit produced.</b> <b>Encryption is
/// pVHD-only, not legacy PBF.</b> The Backup &amp; Recovery 17 / HDM 16
/// manuals state "password protection, backup compression and splitting
/// options are only available for pVHD." The earlier metadata blocker
/// claim of "optional AES payload encryption with vendor KDF" on PBF was
/// incorrect: encryption appeared with pVHD in HDM 14 / Nov 2013. Legacy
/// PBF data blocks are unencrypted; passwords on legacy PBF gate only the
/// UI restore wizard, not the on-disk payload. The blocker is removed
/// from the surfaced list.
/// </para>
///
/// <para>
/// <b>Other diagnostic facts cross-confirmed during the audit (now surfaced
/// in metadata.ini):</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Compression dial 0-9.</b> The four user-visible levels
///     (none / fast / normal / best) map onto a 0-9 backup-compression-level
///     dial per the Scripting Language manual. The on-disk compressor
///     identifier and per-block frame header remain undocumented.
///   </description></item>
///   <item><description>
///     <b>Default 4 GiB split.</b> Backup &amp; Recovery 17 / HDM 16 manuals
///     pin this as the legacy FAT32 4-GiB-file-size workaround.
///   </description></item>
///   <item><description>
///     <b>Conceptual triple.</b> KB 767 + the forum quote consistently describe
///     a backup as {index file (<c>.pfi</c> since HDM 11, <c>.pbf</c> before),
///     metadata sidecar (<c>.pfm</c> for Image Explorer fast-browse),
///     compressed data files (<c>.pbf</c> + <c>.000</c>/<c>.001</c>/...
///     splits)}.
///   </description></item>
///   <item><description>
///     <b>Differential = base + 1 delta, Incremental = base + N chained
///     deltas.</b> KB 262 nomenclature; the per-delta on-disk framing is not
///     disclosed.
///   </description></item>
///   <item><description>
///     <b>exFAT corruption advisory.</b> Paragon support article notes that
///     PBF writers issue many sub-flush writes that can collide with the
///     Microsoft exFAT write-cache flush bug on Win10+. This rules out a
///     simple "write whole file once" container — implies stream-style
///     append framing — but no byte-level framing detail is given.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Conclusion.</b> After twelve research vectors, chunk framing past the
/// 4-byte "PImg" magic remains undocumented in every public source. Stage
/// stays at R/O metadata; the audit trail is persisted in
/// <c>metadata.ini</c> so the next maintainer doesn't redo the same work.
/// </para>
///
/// <para>
/// <b>Sources consulted (all public):</b>
/// </para>
/// <list type="bullet">
///   <item><description>TrID file-identifier database — header signature
///     <c>50 49 6D 67</c> ("PImg") for "Paragon Backup Format image"
///     (cross-referenced via file-extension.net, openthefile.net,
///     recoveryutility.com, datenrettungtool.de).</description></item>
///   <item><description>Paragon Software Knowledge Base article 767
///     (kb.paragon-software.com/article/767, "Archive Formats") — multi-file
///     layout, 2011 HDM 11 index switch, HDM 16+ obsolescence.</description></item>
///   <item><description>Paragon Software Knowledge Base article 262
///     (kb.paragon-software.com/article/262, "Backup Types") — incremental
///     sector-based backup mechanics, pVHD successor.</description></item>
///   <item><description>Paragon Backup &amp; Recovery 17 User Manual and
///     Hard Disk Manager 16 User Manual — confirm PBF restore-only, document
///     "<c>.pfi</c> Refresh / rebuild," pin "password / compression /
///     splitting are pVHD-only."</description></item>
///   <item><description>Paragon Scripting Language User Manual
///     (download.paragon-software.com/doc/script_man_.pdf) — 0-9
///     compression dial, <c>*.pbf</c> exclusion list.</description></item>
///   <item><description>Paragon HDM SDK developer portal
///     (developers.paragon-software.com/hdm-sdk) — scope is partitioning,
///     not backup format.</description></item>
///   <item><description>Paragon-Software-Group + Paragon-Backup-Recovery
///     GitHub organisations — audited, no backup-format code published.</description></item>
///   <item><description>Gary Kessler / SEARCH file-signatures database
///     (garykessler.net) — audited, no PBF entry.</description></item>
///   <item><description>Kaitai Struct format library — audited, no PBF
///     template.</description></item>
///   <item><description>paragon284.rssing.com Drive Backup product-line forum
///     mirror — community {index, metadata, compressed} triple confirmation.</description></item>
///   <item><description>fileinfo.com / file.org / solvusoft.com /
///     filext.com / file-extension.net PBF entries — format classification
///     (Disk Image / proprietary).</description></item>
/// </list>
/// </summary>
public sealed class ParagonReader : IDisposable {

  /// <summary>
  /// Paragon Backup Format image magic: 4 ASCII bytes <c>"PImg"</c>
  /// (hex <c>50 49 6D 67</c>) at offset 0. Documented in TrID and
  /// confirmed by multiple independent file-extension catalogues.
  /// </summary>
  public static readonly byte[] PImgTag = "PImg"u8.ToArray();

  private const int MinHeaderSize = 4;

  private readonly byte[] _data;
  private readonly List<ParagonEntry> _entries = [];

  public IReadOnlyList<ParagonEntry> Entries => _entries;

  /// <summary>Detected magic variant; always <c>"PImg"</c> when <see cref="ValidHeader"/> is true.</summary>
  public string Variant { get; private set; } = "";

  /// <summary>
  /// The 4 bytes immediately following the <c>"PImg"</c> magic, captured as a
  /// little-endian unsigned 32-bit word for diagnostic surfacing in
  /// <c>metadata.ini</c>. The byte layout after the magic is undocumented in
  /// every public source; we expose this word only as an aid for forensic
  /// triage / future RE work, NOT as a parsed version field.
  /// </summary>
  public uint TrailingWord { get; private set; }

  public bool ValidHeader { get; private set; }

  public ParagonReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < MinHeaderSize)
      throw new InvalidDataException("Paragon: file too small for 'PImg' header (need at least 4 bytes).");

    if (!_data.AsSpan(0, 4).SequenceEqual(PImgTag))
      throw new InvalidDataException(
        "Paragon: missing 'PImg' (50 49 6D 67) tag at offset 0 — not a Paragon Backup Format image.");

    this.Variant = "PImg";
    this.ValidHeader = true;

    if (_data.Length >= 8)
      this.TrailingWord = (uint)(_data[4] | (_data[5] << 8) | (_data[6] << 16) | (_data[7] << 24));

    var meta = BuildMetadata();
    _entries.Add(new ParagonEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new ParagonEntry { Name = "paragon-backup.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ro-metadata\n");
    bldr.Append("stage=1\n");
    bldr.Append("format=Paragon Backup & Recovery image (.pbf)\n");
    bldr.Append("vendor=Paragon Software Group (proprietary, closed-source)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_variant={this.Variant}\n");
    bldr.Append("magic_bytes_hex=50 49 6D 67\n");
    bldr.Append("magic_ascii=PImg\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append("magic_source=TrID file-identifier database (cross-checked: file-extension.net, recoveryutility.com, datenrettungtool.de)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");

    // Multi-file companion convention (Paragon KB article 767).
    bldr.Append("companion_pbf=main image / legacy pre-HDM-11 index (.pbf)\n");
    bldr.Append("companion_pfi=Paragon Backup Index Data - main index since HDM 11 / late 2011 (.pfi)\n");
    bldr.Append("companion_pfm=Paragon Backup Image Descriptor - Image Explorer fast-browse sidecar (.pfm)\n");
    bldr.Append("companion_split=Split data chunks at ~4 GB boundary (.000, .001, .002, ...)\n");

    // Format-evolution timeline (Paragon KB article 767 + 262).
    bldr.Append("history_hdm10=PBF is sole index (no PFI yet)\n");
    bldr.Append("history_hdm11=PFI introduced; PBF demoted to data file, PFI is main index (late 2011)\n");
    bldr.Append("history_hdm14=pVHD (Paragon Virtual Hard Disk) introduced as new container; PBF still primary in 'Smart Backup'\n");
    bldr.Append("history_hdm15=pVHD is the default; PBF only via 'Legacy Mode'\n");
    bldr.Append("history_hdm16=PBF is restore-only; new backups can no longer be written in PBF\n");

    // What we still can't do (structural R/W blockers).
    // NB: the legacy Stage-0 -> R/O baseline asserted an "optional AES payload
    // encryption with vendor KDF" blocker. The deep-RE audit retired that:
    // Backup & Recovery 17 / HDM 16 manuals state "password protection,
    // backup compression and splitting options are only available for pVHD."
    // Encryption appeared with pVHD in HDM 14 (Nov 2013), not legacy PBF.
    // Passwords on legacy PBF gate the UI restore wizard only, not the
    // on-disk payload. Blocker is removed.
    bldr.Append("ro_promotion=metadata-only\n");
    bldr.Append("rw_promotion=blocked\n");
    bldr.Append("rw_blocker_1=block index layout after the 'PImg' magic is undocumented in every public source\n");
    bldr.Append("rw_blocker_2=per-cluster allocation bitmap (sector-based backup) is undocumented\n");
    bldr.Append("rw_blocker_3=snapshot / incremental chain framing is undocumented\n");
    bldr.Append("rw_blocker_4=per-segment split-archive trailer (.000/.001/...) is undocumented\n");
    bldr.Append("rw_blocker_5=on-disk compressor identifier and per-block frame header are undocumented (user-visible levels none/fast/normal/best map to 0-9 dial per Scripting Language manual, but the on-wire frame format is not)\n");
    bldr.Append("rw_blocker_6=format is also obsolete for creation since HDM 16; vendor tools restore-only\n");

    // Diagnostic facts the audit cross-confirmed (manuals + KB + forum).
    bldr.Append("fact_compression_levels=0-9 dial: none / fast / normal / best (Paragon Scripting Language manual)\n");
    bldr.Append("fact_default_split=4 GiB (Backup & Recovery 17 + HDM 16 manuals; legacy FAT32 4-GiB workaround)\n");
    bldr.Append("fact_encryption_pvhd_only=password protection / compression / splitting are pVHD-only; legacy PBF data blocks are unencrypted (B&R 17 + HDM 16 manuals)\n");
    bldr.Append("fact_conceptual_triple=backup = {index (.pfi since HDM 11, .pbf before), metadata sidecar (.pfm), compressed data files (.pbf + .000/.001/... splits)} (KB 767 + paragon284 forum)\n");
    bldr.Append("fact_chain_model=Differential = base + 1 delta; Incremental = base + N chained deltas (KB 262); per-delta framing not disclosed\n");
    bldr.Append("fact_exfat_advisory=Paragon support note: PBF writers issue many sub-flush writes that collide with the Microsoft exFAT cache-flush bug on Win10+. Implies append-style framing, not whole-file-once container.\n");

    // Audit trail: research vectors pursued past TrID magic, all dead-ended.
    // Persisted so the next maintainer doesn't repeat the same searches.
    bldr.Append("re_audit_1=asmodean 'expimg' (asmodean.reverse.net/pages/expimg.html) - FALSE LEAD, refers to a Japanese visual-novel archive 'PImg' unrelated to Paragon\n");
    bldr.Append("re_audit_2=Paragon HDM SDK (developers.paragon-software.com/hdm-sdk) - partitioning operations only; hdmengine/hdmclient/biontdrv headers are partition-management, not archive-format\n");
    bldr.Append("re_audit_3=Paragon-Software-Group GitHub - linux-ntfs3, paragon_apfs_sdk_ce, paragon_portable_stl, paragon_firewall_ce, eucalyptus, paragon-lowcode-oss; no backup-format code\n");
    bldr.Append("re_audit_4=Paragon-Backup-Recovery GitHub - profile-only org, no backup-format code\n");
    bldr.Append("re_audit_5=USPTO patent database - no Paragon-Software-Group-assigned patent disclosing PBF on-disk layout; disk-image patents in this space belong to Veritas / Symantec / Acronis\n");
    bldr.Append("re_audit_6=EnCase / X-Ways / FTK forensic-suite custom-type repositories - generic carving only, no Paragon-PBF-specific carver or content-walk recipe\n");
    bldr.Append("re_audit_7=Habr Q&A / Toster.ru threads - community confirms 'PBF is closed, Paragon utilities are the only way'; no chunk-framing detail\n");
    bldr.Append("re_audit_8=paragon284.rssing.com Drive Backup product-line forum - user describes 'Paragon file directory structure: index file, metadata and compressed backup files' confirming the conceptual triple but no byte-level layout\n");
    bldr.Append("re_audit_9=Gary Kessler / SEARCH file-signatures database (garykessler.net) - audited, no PBF entry\n");
    bldr.Append("re_audit_10=Kaitai Struct format library + 010 Editor / Hexinator / Synalize It! / ImHex templates - no .ksy or .bt template for PBF\n");
    bldr.Append("re_audit_11=Paragon Scripting Language User Manual (download.paragon-software.com/doc/script_man_.pdf) - references *.pbf only as an exclusion extension; confirms 0-9 compression dial; no struct layout\n");
    bldr.Append("re_audit_12=Paragon ExtFS / NTFS3 / UFSD / APFS-SDK-CE open-source releases - filesystem drivers, not backup-archive drivers; share no data structures with PBF\n");
    bldr.Append("re_conclusion=Twelve research vectors exhausted. Chunk framing past the 4-byte 'PImg' magic remains undocumented in every public source. Stage stays at R/O metadata.\n");

    bldr.Append("note=R/O metadata-only. The 'PImg' magic at offset 0 is documented (TrID); the on-disk layout after it is not. Restore content with vendor tools (Paragon Backup & Recovery, Hard Disk Manager).\n");
    bldr.Append("references=TrID 'Paragon Backup Format image' (50 49 6D 67),kb.paragon-software.com/article/767 (Archive Formats),kb.paragon-software.com/article/262 (Backup Types),Paragon Backup & Recovery 17 User Manual,Paragon Hard Disk Manager 16 User Manual,Paragon Scripting Language User Manual,developers.paragon-software.com/hdm-sdk,github.com/Paragon-Software-Group,github.com/Paragon-Backup-Recovery,garykessler.net file-signatures table,paragon284.rssing.com Drive Backup forum mirror\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(ParagonEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
