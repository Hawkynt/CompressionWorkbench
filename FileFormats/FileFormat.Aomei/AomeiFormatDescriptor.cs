#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Aomei;

/// <summary>
/// Read/write descriptor for AOMEI Backupper image files (<c>.adi</c> disk /
/// partition / system backup, <c>.afi</c> file/folder backup). Both share
/// the <see cref="AomeiReader.Magic"/> 5-byte ASCII signature at offset 0
/// (the trailing backslash is the low byte of the
/// <see cref="BrFileHead.Size"/> field, not a path separator).
///
/// <para>
/// Implementation ported from the reverse-engineered format spec at
/// <c>docs/AOMEI_FORMAT_SPEC.md</c>: full <c>BIFH</c>/<c>BIFT</c> structs
/// with <c>BRCrc32</c> verification, the recovered
/// <c>BR_STANDARD_HEADER</c> tagged-record framing, and typed views of the
/// four confirmed INFO records (COMPRESS / ENCRYPT / PASSWORD /
/// BACKUP_TYPE).
/// </para>
///
/// <para>
/// <b>What is surfaced for parsable input:</b>
/// </para>
/// <list type="bullet">
///   <item><description><c>FULL.bifh</c> — the raw image bytes (also acts as
///         a fallback for callers that just want the original payload).</description></item>
///   <item><description><c>metadata.ini</c> — parse status, decoded INFO
///         record fields (backup_type, compress method/level, encrypt
///         method/keylen, password MD5), record-walk summary.</description></item>
///   <item><description><c>header.bin</c> — the original 64-byte capture of
///         the file start (preserved from the R/O baseline for backward
///         compatibility with downstream forensic tooling).</description></item>
///   <item><description><c>head.bin</c> / <c>tail.bin</c> — the full 0x65C
///         BIFH and 0x674 BIFT structs, available when the file is long
///         enough to contain them, for future RE work on the as-yet-TODO
///         body fields.</description></item>
///   <item><description><c>record-NN-NAME.bin</c> — every walked
///         <c>BR_STANDARD_HEADER</c>-prefixed record's raw bytes,
///         filename-tagged with its type code. Lets callers inspect the
///         INDEX_TYPE_* records whose body layouts remain TODO.</description></item>
///   <item><description><c>userdata/NAME</c> — when the file was produced by
///         this project's writer, the user-data envelopes
///         (<see cref="AomeiWriter.UserDataTypeTag"/>) are unwrapped and
///         their original filename + payload are emitted under a
///         <c>userdata/</c> prefix.</description></item>
/// </list>
///
/// <para>
/// <b>Create() — round-trip honest:</b> the writer emits a wire-format
/// correct BIFH+INFO+BIFT container with sealed CRCs. The container
/// round-trips through our own reader. <i>It is not byte-compatible with
/// the AOMEI Backupper application</i>: the head/tail body fields (0x650 /
/// 0x668 bytes after the standard header) are zero-filled because their
/// layout is TODO in the recovered spec. Containers produced here advertise
/// compression / encryption / backup-type via standard INFO records, and
/// wrap user inputs in a vendor-namespace
/// <see cref="AomeiWriter.UserDataTypeTag"/> envelope so the project's own
/// reader can extract them again.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.aomeitech.com</c> — vendor — the .adi/.afi container is proprietary and unpublished</description></item>
///   <item><description><c>docs/AOMEI_FORMAT_SPEC.md</c> (this repository) — reverse-engineered BIFH/BIFT + BR_STANDARD_HEADER on-disk spec</description></item>
/// </list>
/// </summary>
public sealed class AomeiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Aomei";
  public string DisplayName => "AOMEI Backupper Image (ADI/AFI)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify;
  public string DefaultExtension => ".adi";
  public IReadOnlyList<string> Extensions => [".adi", ".afi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(AomeiReader.Magic, Offset: 0, Confidence: 0.95f),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("lz4", "LZ4 raw block"),
    new("zlib", "zlib"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AOMEI Backupper disk (.adi) / file (.afi) image — BIFH/BIFT outer container R/W via the " +
    "BR_STANDARD_HEADER tagged-record framing recovered by reverse engineering of the vendor's " +
    "image-format library (source-tree codename BRCloudv2, embedded PDB path " +
    "E:\\BRCloudv2\\src\\ImgFile\\ImageFile.cpp). Reader: verifies BIFH magic+size+CRC32 at offset 0 " +
    "(0x65C bytes), verifies BIFT magic+size+CRC32 at file_size-0x674, walks every " +
    "BR_STANDARD_HEADER-prefixed INFO/INDEX record and surfaces typed views of the four shipped " +
    "INFO records (INFO_TYPE_IMAGE_COMPRESS=0x105, INFO_TYPE_IMAGE_ENCRYPT=0x106, " +
    "INFO_TYPE_IMAGE_PASSWORD=0x107 with MD5(UTF-16LE(password)), INFO_TYPE_BACKUP_TYPE=0x10C). " +
    "Writer: emits wire-format-correct BIFH+INFO+BR_IMAGE_INDEX+BIFT round-trip containers with sealed CRCs. " +
    "R/W: AomeiInPlaceModifier performs true in-place Add / Replace / Remove by appending fresh " +
    "BR_IMAGE_INDEX_ENTRY_VDB entries (0x20 bytes each) at the end of the trailing INDEX_TYPE_DATABLOCK " +
    "(0x202) BR_IMAGE_INDEX. Existing user-data envelope bytes [BIFH end, old index start) and existing " +
    "VDB entries [shipped+0x18, +0x18+oldCount*0x20) stay byte-identical at their original offsets; the " +
    "only patched fields are EntryCount at shipped offset +0x10, the index's BR_STANDARD_HEADER Size " +
    "field, the index's BR_STANDARD_HEADER Crc32 field, and the BIFT (re-emitted at the new tail). " +
    "Replace appends a fresh entry carrying the target's original RegNo; reader picks LATEST entry per " +
    "RegNo. Remove appends a tombstone (NewSize=0xFFFFFFFF sentinel); reader's latest-wins gate drops " +
    "the chunk from the live entry view, original bytes survive at original offset. " +
    "Reverse-engineered scope additions (constants pinned, not yet wired into the on-wire emit): " +
    "(a) the full INFO_TYPE_* enum past the four shipped tags — IMAGE_SPLIT_SIZE=0x104, " +
    "IMAGE_COMMENT=0x108, BACKUP_TIME=0x10B, BACKUP_OPTION=0x10D, DISK_INFO=0x102, " +
    "VOLUME_INFO=0x103, FLB_BACKUP_OPTION=0x113, FLB_BACKUP_OPTION_EX=0x116, FLB_PATH_LIST=0x112; " +
    "(b) the full INDEX_TYPE_* enum — ROOT=0x200, VOLUME=0x201, DATABLOCK=0x202, DIRTREE=0x300, " +
    "DATAAREA=0x301; (c) the vendor BR_STANDARD_HEADER is 16 bytes (Type:u32, Size:u32, Crc32:u32, " +
    "Reserved:u32) — this codebase ships a 12-byte alias of that layout that round-trips through " +
    "its own reader but is not byte-compatible with the AOMEI application; (d) the BR_IMAGE_INDEX " +
    "header carries EntryCount at +0x14, EntrySize at +0x18 and the packed entry array at +0x1C, " +
    "with sizeof(BR_IMAGE_INDEX_ENTRY_VDB)=0x20 for INDEX_TYPE_DATABLOCK records (the per-entry " +
    "field name list — RegNo, BlockNo, ImgOffset, NewSize, OldSize, Crc32 — is recovered but the " +
    "byte offsets of each field within the 32-byte entry are not pinned by passive RE). " +
    "Known limitations: (1) head/tail body fields past the first 12 bytes are zero-filled (the " +
    "file tail is known to carry DataOffInSet:u64 + DataLenInSet:u64 for split-volume bookkeeping " +
    "but their byte offsets are undetermined) so containers are NOT byte-compatible with AOMEI " +
    "Backupper; (2) INDEX_TYPE_DATABLOCK / DATAAREA record bodies are not emitted — payload data " +
    "is wrapped in a vendor-namespace 0xF001 user-data envelope rather than the real index " +
    "framing; (3) AES variant and IV derivation are still undetermined so encryption is " +
    "advertised but not applied; (4) the scheduled-task magic password 'AomeiTech.SchduleTask' " +
    "is recognised but its MD5 substitution requires the runtime scheduler context which is " +
    "unavailable offline.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bifh", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    AomeiReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new AomeiReader(ms);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bifh", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.bifh", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (reader.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "header.bin", reader.HeaderRaw.LongLength, reader.HeaderRaw.LongLength, "stored", false, false, null));
    if (reader.Head is not null)
      entries.Add(new ArchiveEntryInfo(idx++, "head.bin", AomeiConstants.BifhSize, AomeiConstants.BifhSize, "stored", false, false, null));
    if (reader.Tail is not null)
      entries.Add(new ArchiveEntryInfo(idx++, "tail.bin", AomeiConstants.BiftSize, AomeiConstants.BiftSize, "stored", false, false, null));

    // When the container carries a trailing INDEX_TYPE_DATABLOCK record,
    // surface user-data via the latest-wins/tombstone-filtered live VDB
    // entries — that view tracks AomeiInPlaceModifier's mutations.
    // Foreign / pre-index containers fall back to the legacy 0xF001 walk.
    var liveByOffset = new HashSet<ulong>();
    if (reader.DataBlockIndexFileOffset is not null) {
      foreach (var live in reader.ResolveLiveUserData()) {
        liveByOffset.Add(live.EnvelopeOffset);
        var safeName = string.IsNullOrEmpty(live.Name) ? $"reg-{live.RegNo:D3}" : live.Name;
        entries.Add(new ArchiveEntryInfo(idx++, "userdata/" + safeName, live.Payload.LongLength, live.Payload.LongLength, "stored", false, false, null));
      }
    }

    for (var i = 0; i < reader.Records.Count; ++i) {
      var record = reader.Records[i];
      if (record.Header.Type == AomeiWriter.UserDataTypeTag) {
        // Skip envelopes already surfaced via the live VDB view.
        if (liveByOffset.Contains((ulong)record.FileOffset)) continue;
        // Legacy fallback: no index present, surface as-walked.
        if (reader.DataBlockIndexFileOffset is null) {
          var name = AomeiWriter.ReadUserDataName(record.Body);
          var payload = AomeiWriter.ReadUserDataPayload(record.Body);
          var safeName = string.IsNullOrEmpty(name) ? $"entry-{i:D3}" : name;
          entries.Add(new ArchiveEntryInfo(idx++, "userdata/" + safeName, payload.LongLength, payload.LongLength, "stored", false, false, null));
        }
      } else {
        var fname = $"record-{i:D3}-{record.TypeName}.bin";
        entries.Add(new ArchiveEntryInfo(idx++, fname, record.Header.Size, record.Header.Size, "stored", false, false, null));
      }
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    AomeiReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new AomeiReader(ms);
    } catch {
      WriteIfMatch(outputDir, "FULL.bifh", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.bifh", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(reader), files);
    if (reader.Valid)
      WriteIfMatch(outputDir, "header.bin", reader.HeaderRaw, files);
    if (reader.Head is not null)
      WriteIfMatch(outputDir, "head.bin", image[..AomeiConstants.BifhSize], files);
    if (reader.Tail is not null)
      WriteIfMatch(outputDir, "tail.bin", image[^AomeiConstants.BiftSize..], files);

    // When the container carries a trailing INDEX_TYPE_DATABLOCK record,
    // extract user-data via the latest-wins/tombstone-filtered live VDB
    // entries — that view tracks AomeiInPlaceModifier's mutations.
    var liveByOffset = new HashSet<ulong>();
    if (reader.DataBlockIndexFileOffset is not null) {
      foreach (var live in reader.ResolveLiveUserData()) {
        liveByOffset.Add(live.EnvelopeOffset);
        var safeName = string.IsNullOrEmpty(live.Name) ? $"reg-{live.RegNo:D3}" : live.Name;
        WriteIfMatch(outputDir, "userdata/" + safeName, live.Payload, files);
      }
    }

    for (var i = 0; i < reader.Records.Count; ++i) {
      var record = reader.Records[i];
      if (record.Header.Type == AomeiWriter.UserDataTypeTag) {
        if (liveByOffset.Contains((ulong)record.FileOffset)) continue;
        if (reader.DataBlockIndexFileOffset is null) {
          var name = AomeiWriter.ReadUserDataName(record.Body);
          var payload = AomeiWriter.ReadUserDataPayload(record.Body);
          var safeName = string.IsNullOrEmpty(name) ? $"entry-{i:D3}" : name;
          WriteIfMatch(outputDir, "userdata/" + safeName, payload, files);
        }
      } else {
        var fname = $"record-{i:D3}-{record.TypeName}.bin";
        // Rebuild the on-disk bytes of the record from header + body so the
        // raw file matches the original wire bytes exactly.
        var on = new byte[record.Header.Size];
        record.Header.Write(on);
        record.Body.CopyTo(on, AomeiConstants.StandardHeaderSize);
        WriteIfMatch(outputDir, fname, on, files);
      }
    }
  }

  /// <summary>
  /// Creates a fresh AOMEI <c>.adi</c> container at <paramref name="output"/>
  /// wrapping the supplied inputs. The container is built via
  /// <see cref="AomeiWriter"/> with sealed CRCs and round-trips through
  /// <see cref="AomeiReader"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Options honoured:
  /// <list type="bullet">
  ///   <item><description><see cref="FormatCreateOptions.Password"/> — when
  ///         non-empty an <see cref="AomeiConstants.InfoTypeImagePassword"/>
  ///         record carrying MD5(UTF-16LE(password)) is emitted.</description></item>
  ///   <item><description><c>FormatSpecific["backup_type"]</c> — 32-bit
  ///         backup-kind code (default 0). Emits an
  ///         <see cref="AomeiConstants.InfoTypeBackupType"/> record.</description></item>
  ///   <item><description><c>FormatSpecific["compress_method"]</c> /
  ///         <c>FormatSpecific["compress_level"]</c> — compress info codes.
  ///         Default: <see cref="AomeiConstants.CompressMethodNone"/>.</description></item>
  ///   <item><description><c>FormatSpecific["encrypt_method"]</c> /
  ///         <c>FormatSpecific["encrypt_key_len"]</c> — only honoured when
  ///         a password is set.</description></item>
  /// </list>
  /// </para>
  /// <para>
  /// Encryption is <i>advertised</i> via the INFO record but the user-data
  /// payload bytes are stored verbatim because the AES variant and IV
  /// derivation are TODO in the recovered spec (§10.3-4). Callers that
  /// supply a password get an MD5 record and the AOMEI scheduled-task
  /// substitution semantics noted in the spec — they do <i>not</i> get
  /// AES-CBC-encrypted payload bytes.
  /// </para>
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var backupType = (uint?)options.GetOptionInt("backup_type", -1);
    if (backupType == unchecked((uint)-1)) backupType = null;

    var compressMethodRaw = options.GetOptionInt("compress_method", -1);
    (uint Method, uint Level)? compressInfo = null;
    if (compressMethodRaw >= 0) {
      var level = (uint)options.GetOptionInt("compress_level", 0);
      compressInfo = ((uint)compressMethodRaw, level);
    }

    (uint Method, uint KeyLen)? encryptInfo = null;
    if (!string.IsNullOrEmpty(options.Password)) {
      var em = (uint)options.GetOptionInt("encrypt_method", 0);
      var ekl = (uint)options.GetOptionInt("encrypt_key_len", 16);
      encryptInfo = (em, ekl);
    }

    var userData = new List<(string Name, byte[] Data)>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      userData.Add((input.ArchiveName, input.ReadContent()));
    }

    var writer = new AomeiWriter {
      BackupTypeKind = backupType,
      CompressInfo = compressInfo,
      EncryptInfo = encryptInfo,
      Password = options.Password,
      UserData = userData,
    };
    var bytes = writer.Build();
    output.Write(bytes, 0, bytes.Length);
  }

  /// <summary>Appends one or more user-data envelopes + matching VDB
  /// entries to <paramref name="archive"/>. Delegates to
  /// <see cref="AomeiInPlaceModifier.Add"/>.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => AomeiInPlaceModifier.Add(archive, inputs);

  /// <summary>Appends a tombstone VDB entry per name in
  /// <paramref name="entryNames"/>. The name resolves to a RegNo via
  /// <see cref="ResolveRegNoFromName"/>; entries that don't match a
  /// live user-data envelope's name (e.g. <c>userdata/foo.bin</c> or
  /// just <c>foo.bin</c>) raise <see cref="FileNotFoundException"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      var regNo = ResolveRegNoFromName(archive, name);
      AomeiInPlaceModifier.Remove(archive, regNo);
    }
  }

  private static uint ResolveRegNoFromName(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(name);
    var trimmed = name;
    const string Prefix = "userdata/";
    if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
      trimmed = trimmed[Prefix.Length..];
    archive.Position = 0;
    var reader = new AomeiReader(archive);
    foreach (var live in reader.ResolveLiveUserData())
      if (string.Equals(live.Name, trimmed, StringComparison.Ordinal))
        return live.RegNo;
    throw new FileNotFoundException(
      $"Aomei descriptor Remove: no live user-data entry named '{name}' " +
      $"(stripped to '{trimmed}'). Names must match the envelope's stored " +
      "filename, optionally prefixed with 'userdata/'.");
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(AomeiReader r) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={r.ParseStatus}\n");
    if (r.Valid) {
      b.Append("magic=BIFH\\\n");
      b.Append(ic, $"post_magic_u32_le=0x{r.PostMagicWord:X8}\n");
    }
    if (r.Head is { } head) {
      b.Append(ic, $"head_flag=0x{head.Flag:X8}\n");
      b.Append(ic, $"head_size=0x{head.Size:X}\n");
      b.Append(ic, $"head_crc32=0x{head.Crc32:X8}\n");
      b.Append(ic, $"head_crc_valid={(r.HeadCrcValid ? "true" : "false")}\n");
    }
    if (r.Tail is { } tail) {
      b.Append(ic, $"tail_flag=0x{tail.Flag:X8}\n");
      b.Append(ic, $"tail_size=0x{tail.Size:X}\n");
      b.Append(ic, $"tail_crc32=0x{tail.Crc32:X8}\n");
      b.Append(ic, $"tail_crc_valid={(r.TailCrcValid ? "true" : "false")}\n");
    }
    b.Append(ic, $"record_count={r.Records.Count}\n");
    if (r.BackupTypeKind is { } bt)
      b.Append(ic, $"backup_type_kind=0x{bt:X8}\n");
    if (r.CompressMethod is { } cm) {
      b.Append(ic, $"compress_method=0x{cm:X8}\n");
      b.Append(ic, $"compress_level={r.CompressLevel ?? 0}\n");
    }
    if (r.EncryptMethod is { } em) {
      b.Append(ic, $"encrypt_method=0x{em:X8}\n");
      b.Append(ic, $"encrypt_key_len={r.EncryptKeyLen ?? 0}\n");
    }
    if (r.PasswordMd5 is { } md5) {
      var hex = new StringBuilder(md5.Length * 2);
      foreach (var by in md5) hex.AppendFormat(ic, "{0:x2}", by);
      b.Append(ic, $"password_md5={hex}\n");
    }
    // Surface the recovered tag enums for forensic clarity.
    b.Append("vendor_standard_header_size=0x10\n");
    b.Append("shipped_standard_header_alias_size=0x0C\n");
    b.Append("info_types_enum=0x102:DISK_INFO,0x103:VOLUME_INFO,0x104:IMAGE_SPLIT_SIZE,");
    b.Append("0x105:IMAGE_COMPRESS,0x106:IMAGE_ENCRYPT,0x107:IMAGE_PASSWORD,0x108:IMAGE_COMMENT,");
    b.Append("0x109:VOLUME_DATA_REGION,0x10B:BACKUP_TIME,0x10C:BACKUP_TYPE,0x10D:BACKUP_OPTION,");
    b.Append("0x110:FLB_SUB_ENTRY_LIST,0x111:FLB_FILE_DATA_BLOCK_LIST,0x112:FLB_PATH_LIST,");
    b.Append("0x113:FLB_BACKUP_OPTION,0x116:FLB_BACKUP_OPTION_EX\n");
    b.Append("index_types_enum=0x200:ROOT,0x201:VOLUME,0x202:DATABLOCK,0x300:DIRTREE,0x301:DATAAREA\n");
    b.Append("index_entry_layout_offsets=entry_count:+0x14,entry_size:+0x18,entries:+0x1C\n");
    b.Append("vdb_entry_size=0x20\n");
    b.Append("vdb_entry_field_names=RegNo,BlockNo,ImgOffset,NewSize,OldSize,Crc32\n");
    // Per-field byte offsets pinned in this commit; see AomeiConstants for provenance.
    b.Append("vdb_entry_field_offsets=RegNo:+0x00:u32,BlockNo:+0x04:u64,ImgOffset:+0x0C:u64,OldSize:+0x14:u32,NewSize:+0x18:u32,Crc32:+0x1C:u32\n");
    b.Append("volume_data_region_size=0x30\n");
    b.Append("head_body_layout=undocumented_past_first_12_bytes\n");
    // BIFT body: DataLenInSet at +0x620, DataOffInSet at +0x628 pinned by
    // (read|write)-side `mov reg,[edi+0xc80..0xc8c]` triangulated against
    // the m_Tail base at object offset 0x660.
    b.Append("tail_body_layout=DataLenInSet:u64@+0x620,DataOffInSet:u64@+0x628,trailing_BR_STANDARD_HEADER@+0x664\n");
    b.Append("tail_trailing_header_offsets=Reserved:+0x664,Crc32:+0x668,Size:+0x66C,Flag:+0x670\n");
    b.Append("index_body_layout=BR_IMAGE_INDEX_header_pinned_VDB_field_offsets_pinned_FDB_size_undetermined\n");
    b.Append("aes_variant_and_iv=undetermined\n");
    // R/W: the in-place modifier patches the trailing INDEX_TYPE_DATABLOCK.
    b.Append("rw_scope=Add / Replace / Remove via true in-place VDB-entry append in the trailing INDEX_TYPE_DATABLOCK BR_IMAGE_INDEX (AomeiInPlaceModifier). User-data envelopes in [BIFH end, old index start) and VDB entries [shipped+0x18, +0x18+oldCount*0x20) stay byte-identical. Replace appends fresh entry with same RegNo (latest-wins). Remove appends tombstone (NewSize=0xFFFFFFFF sentinel); reader's latest-wins gate hides the entry, original envelope bytes survive.\n");
    b.Append("shipped_index_layout_offsets=entry_count:+0x10,entry_size:+0x14,entries:+0x18\n");
    b.Append("tombstone_new_size_sentinel=0xFFFFFFFF\n");
    if (r.DataBlockIndexFileOffset is { } idxOff)
      b.Append(ic, $"datablock_index_file_offset=0x{idxOff:X}\n");
    if (r.DataBlockIndexSize is { } idxSize)
      b.Append(ic, $"datablock_index_size=0x{idxSize:X}\n");
    b.Append(ic, $"datablock_index_all_entry_count={r.AllVdbEntries.Count}\n");
    b.Append(ic, $"datablock_index_live_entry_count={r.LiveVdbEntries.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Reader uses the same per-instance cap as AomeiReader.
  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < AomeiReader.MaxImageBytes && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
