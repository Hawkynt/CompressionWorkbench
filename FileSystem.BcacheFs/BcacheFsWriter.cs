#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.BcacheFs;

/// <summary>
/// Writes a WORM-minimal BcacheFS image: a spec-compliant primary superblock
/// at byte offset 4096 (sector 8), the canonical four-copy <c>bch_sb_layout</c>
/// describing the backup superblock locations, and three SB sections:
/// <c>BCH_SB_FIELD_members_v1</c> (single device), <c>BCH_SB_FIELD_replicas_v0</c>
/// (btree+journal on dev[0]), and a header-only <c>BCH_SB_FIELD_errors</c>.
/// The image is sized so every backup-superblock slot named in the layout
/// actually fits inside the file (<see cref="MinImageSize"/> = 128 MiB by
/// default — required because <c>BCH_MIN_NR_NBUCKETS</c> = 512 paired with
/// our 256 KiB bucket size needs at least 128 MiB).
/// </summary>
/// <remarks>
/// <para>
/// Spec source: <c>fs/bcachefs/bcachefs_format.h</c> (kernel) and
/// <c>libbcachefs/sb-members_format.h</c> (bcachefs-tools). Field offsets
/// follow the actual struct layout, NOT the looser interpretation an earlier
/// revision of the read-only descriptor was using:
/// </para>
/// <list type="bullet">
///   <item><c>csum</c> is 16 bytes (u64 lo + u64 hi), not 8.</item>
///   <item><c>version</c> / <c>version_min</c> are <c>__le16</c>, not u64,
///         encoded as <c>(major &lt;&lt; 10) | minor</c>.</item>
///   <item><c>nr_devices</c> is a single byte at offset 123, not a u32.</item>
///   <item>The layout struct lives inline at offset 240 of the superblock,
///         and a duplicate copy lives at sector 7 (file offset 3584).</item>
/// </list>
/// <para>
/// Scope: this writer satisfies <c>bcachefs show-super</c> on the resulting
/// image. <c>bcachefs fsck</c> will still reject with
/// <c>insufficient_devices</c> — the alloc btree is absent, as are the 8
/// other on-disk btree roots, journal entries, compat-feature bits, and
/// the <c>clean</c>/<c>journal_v2</c>/<c>counters</c>/<c>members_v2</c> SB
/// sections. Reaching fsck-clean is multi-week kernel-spec work tracked in
/// <c>docs/FILESYSTEMS.md</c>.
/// </para>
/// </remarks>
public sealed class BcacheFsWriter {
  // ── Spec constants ────────────────────────────────────────────────

  /// <summary>BCHFS_MAGIC — c68573f6-66ce-90a9-d96a-60cf-803d-f7ef in storage byte order.</summary>
  public static readonly byte[] BcachefsMagic = [
    0xC6, 0x85, 0x73, 0xF6,
    0x66, 0xCE,
    0x90, 0xA9,
    0xD9, 0x6A,
    0x60, 0xCF, 0x80, 0x3D, 0xF7, 0xEF,
  ];

  /// <summary>Sector at which <c>bch_sb_layout</c> is written (per kernel).</summary>
  internal const int BchSbLayoutSector = 7;

  /// <summary>Sector at which the primary <c>bch_sb</c> is written (= 4096 bytes).</summary>
  internal const int BchSbSector = 8;

  /// <summary>Size of bch_sb_layout struct on disk = 16 (magic) + 8 (header) + 488 (61 × u64 sb_offset[]).</summary>
  internal const int LayoutStructSize = 16 + 8 + 61 * 8;

  /// <summary>Offset of <c>layout</c> field within <c>struct bch_sb</c>.</summary>
  internal const int LayoutOffsetInSb = 240;

  /// <summary>Total byte size of the fixed bch_sb area (everything before <c>start[0]</c>).</summary>
  internal const int FixedSbBytes = LayoutOffsetInSb + LayoutStructSize; // 240 + 512 = 752

  /// <summary>BCH_SB_FIELD_members_v1 type tag (per BCH_SB_FIELDS x-list at index 1).</summary>
  /// <remarks>
  /// Older bcachefs-tools (≤ 1.3.x) only check for members_v1 in
  /// <c>bch2_sb_validate</c>. Newer kernels fall back from v2 → v1 when v2
  /// is absent. We therefore emit the v1 form, which every parser accepts.
  /// </remarks>
  internal const uint MembersV1FieldType = 1;

  /// <summary>BCH_SB_FIELD_replicas_v0 type tag (per BCH_SB_FIELDS x-list at index 3).</summary>
  /// <remarks>
  /// Declares which devices hold each data type's replicas. For an empty
  /// single-device image, mkfs.bcachefs writes two entries: btree on dev[0]
  /// and journal on dev[0] (each a 3-byte struct: u8 data_type + u8 nr_devs
  /// + u8[] devs).
  /// </remarks>
  internal const uint ReplicasV0FieldType = 3;

  /// <summary>BCH_SB_FIELD_errors type tag (per BCH_SB_FIELDS x-list at index 12).</summary>
  /// <remarks>
  /// Empty bitmap of recently-seen filesystem errors. mkfs.bcachefs emits a
  /// header-only section (u64s=1, no data) on a fresh filesystem.
  /// </remarks>
  internal const uint ErrorsFieldType = 12;

  /// <summary>
  /// Per-entry member byte count for the v1 layout (matches the older
  /// <c>struct bch_member</c> the bcachefs-tools 1.3.x parser compiles with):
  /// uuid(16) + nbuckets(8) + first_bucket(2) + bucket_size(2) + pad(4) +
  /// last_mount(8) + flags(8) + iops[4](16) + errors[3](24) +
  /// errors_at_reset[3](24) + errors_reset_time(8) = 120 bytes.
  /// </summary>
  internal const int MemberBytesPerEntry = 120;

  /// <summary>
  /// BCH_VERSION(1, 3) — "rebalance_work", the latest version recognised by
  /// the widely-deployed bcachefs-tools 1.3.x line. Newer kernels accept
  /// older versions transparently; older tools reject newer versions with
  /// "Unsupported superblock version" so we err on the side of broad
  /// compatibility.
  /// </summary>
  internal const ushort SbVersion = (1 << 10) | 3;

  /// <summary>bcachefs_metadata_version_min — every kernel ≥ 6.7 accepts this minimum.</summary>
  internal const ushort SbVersionMin = 9;

  /// <summary>
  /// Default image size = 128 MiB. Lower bound is enforced by:
  /// (1) the layout's four backup-SB slots needing ~32 KiB at the file end,
  /// and (2) <c>BCH_MIN_NR_NBUCKETS</c> = 512 paired with our 256-KiB
  /// bucket size (= 512 × 256 KiB = 128 MiB minimum). Smaller images would
  /// be rejected by <c>bcachefs show-super</c> with "Not enough buckets".
  /// </summary>
  public const long MinImageSize = 128L * 1024 * 1024;

  /// <summary>BCH_SB_LAYOUT_SIZE_BITS = 16 → max sb size = 2^16 sectors = 32 MiB; we cap at 4 KiB = 3.</summary>
  internal const byte SbMaxSizeBits = 3; // 2^3 = 8 sectors = 4 KiB; comfortable for our 4-KiB-or-less SB

  /// <summary>Number of backup superblock slots advertised in the layout.</summary>
  internal const byte NrSuperblocks = 4;

  // ── State ─────────────────────────────────────────────────────────

  private readonly List<(string Name, byte[] Data)> _files = [];
  private string _label = "cwb-bcachefs";
  private long _imageSize = MinImageSize;
  private Guid _internalUuid = Guid.NewGuid();
  private Guid _userUuid = Guid.NewGuid();

  /// <summary>Sets the volume label (max 31 ASCII bytes — truncated and NUL-padded into label[32]).</summary>
  public void SetLabel(string label) {
    ArgumentNullException.ThrowIfNull(label);
    this._label = label;
  }

  /// <summary>Overrides the auto-generated internal UUID. Must be non-zero or the kernel rejects the image.</summary>
  public void SetInternalUuid(Guid uuid) => this._internalUuid = uuid;

  /// <summary>Overrides the auto-generated user-facing UUID. Must be non-zero or the kernel rejects the image.</summary>
  public void SetUserUuid(Guid uuid) => this._userUuid = uuid;

  /// <summary>Sets the total image size in bytes. Must be ≥ <see cref="MinImageSize"/> so all backup SBs fit.</summary>
  public void SetImageSize(long bytes) {
    if (bytes < MinImageSize)
      throw new ArgumentOutOfRangeException(nameof(bytes),
        $"Image must be at least {MinImageSize} bytes ({MinImageSize / (1024 * 1024)} MiB) so all four backup superblocks fit.");
    this._imageSize = bytes;
  }

  /// <summary>
  /// Records a file to surface in metadata only. The current writer does not
  /// emit B-tree nodes — file content is NOT recoverable from the resulting
  /// image, but the file list is captured so future scope expansion can pick
  /// up where this writer leaves off.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._files.Count >= 100)
      throw new InvalidOperationException("WORM-minimal BcacheFs writer caps at 100 files (no real B-tree yet).");
    this._files.Add((name, data));
  }

  /// <summary>Emits the spec-compliant image to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = new byte[this._imageSize];

    // Build the canonical 4-slot layout. Slot 0 holds the primary SB; slots
    // 1..3 are the backup SBs that fsck looks for after a corrupted primary.
    // Each slot reserves (1 << SbMaxSizeBits) sectors so the kernel's
    // overlap check passes.
    var sbSlotSectors = 1 << SbMaxSizeBits; // 8 sectors = 4 KiB
    var totalSectors = this._imageSize / 512;
    long[] sbOffsetsSectors = [
      BchSbSector,                         // 8        — primary @ byte 4096
      BchSbSector + sbSlotSectors,         // 16       — backup 1 @ byte 8192
      totalSectors - 2 * sbSlotSectors - 512, // end-256k area  — backup 2
      totalSectors - sbSlotSectors - 256,     // end-128k area  — backup 3
    ];

    // Build the 752-byte primary SB once, then stamp the same bytes at every
    // sb_offset[] in the layout. The kernel cross-validates them when
    // recovering from primary-SB corruption.
    var sb = this.BuildSuperblock(sbOffsetsSectors[0]);

    // Stamp the layout struct at sector 7 (the kernel reads this *first*
    // to discover where the SBs live).
    var layoutBlock = new byte[LayoutStructSize];
    WriteLayout(layoutBlock, sbOffsetsSectors);
    Array.Copy(layoutBlock, 0, image, BchSbLayoutSector * 512, LayoutStructSize);

    // Stamp the SB at every advertised slot. Each copy carries its own
    // self-describing offset field so bch2_sb_validate() can cross-check.
    foreach (var sectorOff in sbOffsetsSectors) {
      var byteOff = sectorOff * 512;
      if (byteOff + sb.Length > image.LongLength)
        throw new InvalidOperationException(
          $"Image too small for backup superblock at sector {sectorOff} (byte {byteOff}, image {image.LongLength}).");

      // Re-stamp the offset field (bytes 104..112) with this slot's sector
      // address — the kernel rejects an SB whose sb->offset disagrees with
      // the sector it was read from.
      var slotCopy = (byte[])sb.Clone();
      BinaryPrimitives.WriteUInt64LittleEndian(slotCopy.AsSpan(104, 8), (ulong)sectorOff);
      Array.Copy(slotCopy, 0, image, byteOff, slotCopy.Length);
    }

    output.Write(image);
  }

  // ── Superblock ────────────────────────────────────────────────────

  /// <summary>
  /// Builds a single superblock (variable-length: 752 bytes fixed area + the
  /// members_v2 section). The csum field at the start is left zero — we
  /// declare BCH_SB_CSUM_TYPE = 0 (none) in flags[0] bits 2..8.
  /// </summary>
  private byte[] BuildSuperblock(long primarySbSector) {
    // Variable-area layout (every section is u64-aligned):
    //   members_v1: 8-byte header + 120-byte member         = 128 bytes
    //   replicas_v0: 8-byte header + 6 bytes data + 2 pad   = 16 bytes
    //                   (entry1: btree/dev=[0]; entry2: journal/dev=[0])
    //   errors: 8-byte header (u64s=1, no data)              = 8 bytes
    // Total variable area = 152 bytes (19 u64s).
    const int membersSectionLen  = 8 + MemberBytesPerEntry; // 128
    const int replicasSectionLen = 16;                       // 8 hdr + 8 (3+3+2 pad)
    const int errorsSectionLen   = 8;                        // header only
    const int variableLen        = membersSectionLen + replicasSectionLen + errorsSectionLen;
    var totalLen = FixedSbBytes + variableLen;               // 752 + 152 = 904 bytes (113 u64s)
    var sb = new byte[totalLen];
    var span = sb.AsSpan();

    // ── Fixed header per struct bch_sb (from fs/bcachefs/bcachefs_format.h) ──
    // 0..16   csum (u64 lo + u64 hi) — left zero (csum_type = none)
    // 16      version (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(16, 2), SbVersion);
    // 18      version_min (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), SbVersionMin);
    // 20..24  pad[2] — left zero
    // 24..40  magic (uuid)
    BcachefsMagic.CopyTo(span.Slice(24, 16));
    // 40..56  uuid (internal — never zero)
    WriteGuid(span.Slice(40, 16), this._internalUuid);
    // 56..72  user_uuid (never zero)
    WriteGuid(span.Slice(56, 16), this._userUuid);
    // 72..104 label[32]
    WriteLabel(span.Slice(72, 32), this._label);
    // 104..112 offset (sector address of THIS sb copy — primary by default)
    BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(104, 8), (ulong)primarySbSector);
    // 112..120 seq (≥1 — highest seq wins on conflict resolution)
    BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(112, 8), 1UL);
    // 120..122 block_size (u16) in sectors. 1 = 512 B (smallest valid).
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(120, 2), 1);
    // 122 dev_idx (u8) — this device's index inside the members array
    sb[122] = 0;
    // 123 nr_devices (u8) — single device
    sb[123] = 1;
    // 124..128 u64s (u32) — count of u64 cells in the variable area after FixedSbBytes
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(124, 4), (uint)(variableLen / 8));
    // 128..136 time_base_lo (u64) — nanoseconds since epoch reference
    BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(128, 8), 0UL);
    // 136..140 time_base_hi (u32)
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(136, 4), 0U);
    // 140..144 time_precision (u32) — MUST be 1..NSEC_PER_SEC (1e9)
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(140, 4), 1U);
    // 144..200 flags[7] (u64×7) — every option whose `bch2_opt_table[].min > 0`
    // MUST be stamped here, otherwise `bch2_opt_validate` fails with
    // ERANGE_option_too_small during show-super / fsck. We mirror what
    // mkfs.bcachefs would write for an all-defaults filesystem.
    WriteSbOptions(span.Slice(144, 56));
    // 200..208 write_time (u64) — left zero
    // 208..224 features[2] (u64×2) — left zero (no incompat features)
    // 224..240 compat[2] (u64×2) — left zero
    // 240..752 layout (bch_sb_layout) — written below

    // Inline layout copy. The kernel cross-references this with the
    // sector-7 layout and rejects mismatches.
    var sbOffsets = new long[NrSuperblocks];
    var sbSlotSectors = 1 << SbMaxSizeBits;
    var totalSectors = this._imageSize / 512;
    sbOffsets[0] = BchSbSector;
    sbOffsets[1] = BchSbSector + sbSlotSectors;
    sbOffsets[2] = totalSectors - 2 * sbSlotSectors - 512;
    sbOffsets[3] = totalSectors - sbSlotSectors - 256;
    WriteLayout(span.Slice(LayoutOffsetInSb, LayoutStructSize), sbOffsets);

    // ── Variable-length section: BCH_SB_FIELD_members_v1 ────────────
    // bch_sb_field header: u32 u64s, u32 type. u64s = u64 cells of THIS
    // section *including* the field header (per vstruct_bytes spec).
    var membersStart = FixedSbBytes;
    var membersU64s = (uint)(membersSectionLen / 8);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(membersStart + 0, 4), membersU64s);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(membersStart + 4, 4), MembersV1FieldType);
    // First (and only) member entry follows immediately at +8 inside the
    // section (no v2 size header — that's the v1/v2 difference).
    var memberStart = membersStart + 8;
    var memberSpan = span.Slice(memberStart, MemberBytesPerEntry);
    // bch_member layout (v1 = 120 bytes; tools 1.3.x compile-time form):
    //   0..16   uuid (device UUID)
    WriteGuid(memberSpan.Slice(0, 16), this._internalUuid);
    //   16..24  nbuckets — bucket count for this device. Must be ≥ BCH_MIN_NR_NBUCKETS = 512.
    //   Bucket size MUST be ≥ btree_node_size (512 sectors = 256 KiB) — otherwise
    //   bcachefs rejects with "bucket size N smaller than btree node size 512".
    var bucketSize = 512; // sectors per bucket = 256 KiB (matches btree_node_size default)
    var nbuckets = (ulong)Math.Max(512, this._imageSize / 512 / bucketSize);
    BinaryPrimitives.WriteUInt64LittleEndian(memberSpan.Slice(16, 8), nbuckets);
    //   24..26  first_bucket — index of first bucket used (0)
    BinaryPrimitives.WriteUInt16LittleEndian(memberSpan.Slice(24, 2), 0);
    //   26..28  bucket_size (sectors) — 16 sectors = 8 KiB
    BinaryPrimitives.WriteUInt16LittleEndian(memberSpan.Slice(26, 2), (ushort)bucketSize);
    //   28..32  pad (u32) — left zero
    //   32..40  last_mount (time_t) — left zero
    //   40..48  flags — left zero (state=rw, group=0, durability=0)
    //   48..64  iops[4] — left zero
    //   64..88  errors[3] — left zero
    //   88..112 errors_at_reset[3] — left zero
    //   112..120 errors_reset_time — left zero

    // ── Variable section: BCH_SB_FIELD_replicas_v0 ──────────────────
    // Two entries advertise that the single device holds both btree and
    // journal replicas. Per <c>fs/bcachefs/bcachefs_format.h</c> the entry
    // shape is `struct bch_replicas_entry_v0 { u8 data_type; u8 nr_devs;
    // u8 devs[]; }`. data_type values: 2=journal, 3=btree (BCH_DATA_TYPES).
    var replicasStart = membersStart + membersSectionLen;
    var replicasU64s = (uint)(replicasSectionLen / 8);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(replicasStart + 0, 4), replicasU64s);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(replicasStart + 4, 4), ReplicasV0FieldType);
    // entry 1: btree on dev[0]
    sb[replicasStart + 8] = 3; // data_type = btree
    sb[replicasStart + 9] = 1; // nr_devs
    sb[replicasStart + 10] = 0; // devs[0]
    // entry 2: journal on dev[0]
    sb[replicasStart + 11] = 2; // data_type = journal
    sb[replicasStart + 12] = 1; // nr_devs
    sb[replicasStart + 13] = 0; // devs[0]
    // bytes 14..15 are u64 alignment padding (already zero)

    // ── Variable section: BCH_SB_FIELD_errors ───────────────────────
    // Header-only (no error history). Required by bcachefs ≥ 1.3.x to
    // confirm we're using the post-`SB_FIELD_errors` schema.
    var errorsStart = replicasStart + replicasSectionLen;
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(errorsStart + 0, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(errorsStart + 4, 4), ErrorsFieldType);

    return sb;
  }

  /// <summary>
  /// Writes a <c>bch_sb_layout</c> into the supplied 512-byte span at the
  /// canonical offset 0. The same layout struct also lives inline in
  /// <c>struct bch_sb</c>.
  /// </summary>
  private static void WriteLayout(Span<byte> dst, long[] sbOffsetsSectors) {
    if (dst.Length < LayoutStructSize)
      throw new ArgumentException("layout span must be at least 512 bytes", nameof(dst));
    if (sbOffsetsSectors.Length == 0 || sbOffsetsSectors.Length > 61)
      throw new ArgumentOutOfRangeException(nameof(sbOffsetsSectors));

    // 0..16  magic — same UUID as the sb's magic field
    BcachefsMagic.CopyTo(dst.Slice(0, 16));
    // 16     layout_type — 0 (the only defined type today)
    dst[16] = 0;
    // 17     sb_max_size_bits — log2(sectors-per-sb-slot)
    dst[17] = SbMaxSizeBits;
    // 18     nr_superblocks — count of valid sb_offset[] entries
    dst[18] = (byte)sbOffsetsSectors.Length;
    // 19..24 pad[5] — left zero
    // 24..512 sb_offset[61] (u64 LE)
    for (var i = 0; i < sbOffsetsSectors.Length; i++)
      BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(24 + 8 * i, 8), (ulong)sbOffsetsSectors[i]);
  }

  /// <summary>
  /// Stamps the option defaults into <c>flags[7]</c>. The kernel's
  /// <c>bch2_opt_validate</c> iterates every SB-stored option and rejects
  /// any value below the option's <c>min</c> with
  /// <c>ERANGE_option_too_small</c>; we therefore initialise every option
  /// with min &gt; 0 to its mkfs.bcachefs default. Bit ranges come from
  /// <c>fs/bcachefs/bcachefs_format.h</c> LE64_BITMASK declarations; default
  /// values come from <c>libbcachefs/opts.h</c> BCH_OPTS x-list.
  /// </summary>
  private static void WriteSbOptions(Span<byte> flags56) {
    if (flags56.Length < 56) throw new ArgumentException("flags span < 56", nameof(flags56));
    // Read each flags[i] u64, OR-in the option, write back.
    Span<ulong> flags = stackalloc ulong[7];
    // (all start at 0)

    // ── flags[0] ────────────────────────────────────────────────────
    // BCH_SB_BTREE_NODE_SIZE @ bits 12..28 (16 bits): on-disk unit = sectors,
    //   default 256 KiB / 512 B = 512 sectors.
    SetBits(ref flags[0], 12, 28, 512);
    // BCH_SB_GC_RESERVE @ bits 28..33 (5 bits): %, min 5, default 8.
    SetBits(ref flags[0], 28, 33, 8);
    // BCH_SB_META_REPLICAS_WANT @ bits 48..52: count, min 1, default 1.
    SetBits(ref flags[0], 48, 52, 1);
    // BCH_SB_DATA_REPLICAS_WANT @ bits 52..56: count, min 1, default 1.
    SetBits(ref flags[0], 52, 56, 1);

    // ── flags[1] ────────────────────────────────────────────────────
    // BCH_SB_ENCODED_EXTENT_MAX_BITS @ bits 14..20 (6 bits): stored as
    //   ilog2(sectors). Default 256 KiB → 512 sectors → ilog2 = 9.
    SetBits(ref flags[1], 14, 20, 9);
    // BCH_SB_META_REPLICAS_REQ @ bits 20..24: count, min 1, default 1.
    SetBits(ref flags[1], 20, 24, 1);
    // BCH_SB_DATA_REPLICAS_REQ @ bits 24..28: count, min 1, default 1.
    SetBits(ref flags[1], 24, 28, 1);

    // ── flags[6] ────────────────────────────────────────────────────
    // BCH_SB_WRITE_ERROR_TIMEOUT @ bits 4..14: seconds, min 1, default 30.
    SetBits(ref flags[6], 4, 14, 30);
    // BCH_SB_CSUM_ERR_RETRY_NR @ bits 14..20: count, min 0, default 3.
    //   Min is 0 so technically not required, but mkfs writes 3 — match.
    SetBits(ref flags[6], 14, 20, 3);

    for (var i = 0; i < 7; i++)
      BinaryPrimitives.WriteUInt64LittleEndian(flags56.Slice(8 * i, 8), flags[i]);
  }

  /// <summary>OR-stamps <paramref name="value"/> into <paramref name="word"/> at bits [<paramref name="lo"/>, <paramref name="hi"/>).</summary>
  private static void SetBits(ref ulong word, int lo, int hi, ulong value) {
    var width = hi - lo;
    var mask = width >= 64 ? ulong.MaxValue : ((1UL << width) - 1UL);
    word = (word & ~(mask << lo)) | ((value & mask) << lo);
  }

  // ── Helpers ───────────────────────────────────────────────────────

  private static void WriteGuid(Span<byte> dst, Guid g) {
    if (dst.Length < 16) throw new ArgumentException("guid span < 16", nameof(dst));
    var bytes = g.ToByteArray();
    bytes.CopyTo(dst);
  }

  private static void WriteLabel(Span<byte> dst, string label) {
    dst.Clear();
    var maxBytes = dst.Length - 1; // leave room for trailing NUL
    var encoded = Encoding.UTF8.GetBytes(label);
    var copyLen = Math.Min(maxBytes, encoded.Length);
    encoded.AsSpan(0, copyLen).CopyTo(dst);
  }
}
