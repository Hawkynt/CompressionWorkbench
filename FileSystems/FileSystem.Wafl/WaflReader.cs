#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry.Streaming;

namespace FileSystem.Wafl;

/// <summary>
/// Read-only structural reader for a flat logical NetApp WAFL volume image.
/// </summary>
/// <remarks>
/// <para>
/// NetApp documents volinfo as the WAFL superblock, with redundant copies at
/// volume block numbers 1 and 2 and block-type magic <c>0xdab8fbab</c>. Later
/// NetApp patents also disclose that volinfo starts with backward-compatible
/// fsinfo magic/version fields and contains a VBN lookup table whose entry zero
/// references the active fsinfo block.
/// </para>
/// <para>
/// Stage 1 is deliberately narrower than "WAFL reader": for the disclosed
/// classic 32-bit direct-fsinfo lookup-table profile, candidate VBN pointers are
/// accepted only when their target block starts with the fsinfo compatibility
/// magic copied into volinfo. No inode offsets, directory-entry sizes, FlexVol
/// container maps, RAID placement or free-space semantics are guessed. Images
/// that cannot satisfy those structural checks remain at Stage 0.
/// </para>
/// </remarks>
public sealed class WaflReader : IDisposable {

  /// <summary>
  /// Legacy public alias retained for API compatibility. It now contains the
  /// documented volinfo magic in big-endian byte order; the former ASCII
  /// <c>"wafd"</c> value was not a published WAFL signature.
  /// </summary>
  [Obsolete("Use WAFL volinfo validation through WaflReader; the magic has no stable fixed byte offset across all ONTAP generations.")]
  public static readonly byte[] FsInfoTag = [0xDA, 0xB8, 0xFB, 0xAB];

  /// <summary>The allocation block size used by the published WAFL format design.</summary>
  public const int BlockSize = 4096;

  private const int FirstVolInfoVbn = 1;
  private const int SecondVolInfoVbn = 2;
  private const uint VolInfoMagic = 0xDAB8FBAB;
  private const int MinimumImageSize = (SecondVolInfoVbn + 1) * BlockSize;
  private const int ClassicVbnSize = sizeof(uint);
  private const int MaxClassicFsInfoPointers = 256; // active + the 255 PCPIs disclosed by US7313720

  private readonly Stream _stream;
  private readonly bool _ownsStream;
  private readonly long _origin;
  private readonly long _imageSize;
  private readonly List<WaflEntry> _entries = [];
  private readonly List<uint> _fsInfoVbns = [];
  private bool _disposed;
  private VolInfoProbe? _firstVolInfo;
  private VolInfoProbe? _secondVolInfo;
  private FsInfoTableProbe? _firstFsInfoTable;
  private FsInfoTableProbe? _secondFsInfoTable;

  /// <summary>Gets the entries surfaced by the structural reader.</summary>
  public IReadOnlyList<WaflEntry> Entries => this._entries;

  /// <summary>
  /// Gets the volinfo version from the first valid superblock copy. When VBN 1
  /// is damaged, the value is taken from VBN 2.
  /// </summary>
  public uint Version { get; private set; }

  /// <summary>Gets a value indicating whether at least one documented volinfo superblock was recognized.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Gets the structural parsing stage: 0 is volinfo detection only; 1 means at
  /// least one volinfo copy yielded a structurally verified direct fsinfo root.
  /// </summary>
  public int Stage { get; private set; }

  /// <summary>
  /// Gets the active fsinfo VBN when all usable redundant volinfo copies agree,
  /// or when only one usable volinfo copy remains. Null means no safe consensus.
  /// </summary>
  public uint? ActiveFsInfoVbn { get; private set; }

  /// <summary>Gets every structurally verified fsinfo VBN reached from a recognized lookup table.</summary>
  public IReadOnlyList<uint> FsInfoVbns => this._fsInfoVbns;

  /// <summary>Initializes a new instance of <see cref="WaflReader"/>.</summary>
  public WaflReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("WAFL: input stream must be readable.", nameof(stream));

    if (stream.CanSeek) {
      this._stream = stream;
      this._origin = stream.Position;
      this._imageSize = stream.Length - this._origin;
    } else {
      var copy = new MemoryStream();
      stream.CopyTo(copy);
      copy.Position = 0;
      this._stream = copy;
      this._ownsStream = true;
      this._origin = 0;
      this._imageSize = copy.Length;
    }

    this.Parse();
  }

  private void Parse() {
    if (this._imageSize < MinimumImageSize)
      throw new InvalidDataException($"WAFL: file too small to contain complete volinfo blocks at VBNs {FirstVolInfoVbn} and {SecondVolInfoVbn}.");

    var copies = GC.AllocateUninitializedArray<byte>(BlockSize * 2);
    var saved = this._stream.Position;
    try {
      this._stream.Position = checked(this._origin + FirstVolInfoVbn * (long)BlockSize);
      this._stream.ReadExactly(copies);
    } finally {
      this._stream.Position = saved;
    }

    var firstBlock = copies.AsSpan(0, BlockSize);
    var secondBlock = copies.AsSpan(BlockSize, BlockSize);
    this._firstVolInfo = ProbeVolInfo(firstBlock);
    this._secondVolInfo = ProbeVolInfo(secondBlock);

    var selected = this._firstVolInfo ?? this._secondVolInfo;
    if (selected is null)
      throw new InvalidDataException("WAFL: neither VBN 1 nor VBN 2 contains the documented volinfo magic 0xdab8fbab.");

    this.Version = selected.Value.Version;
    this.ValidHeader = true;

    if (this._firstVolInfo is { } first)
      this._firstFsInfoTable = this.ProbeClassicFsInfoTable(firstBlock, first);
    if (this._secondVolInfo is { } second)
      this._secondFsInfoTable = this.ProbeClassicFsInfoTable(secondBlock, second);

    this.PromoteStructuralStage();

    var metadata = this.BuildMetadata();
    this._entries.Add(new WaflEntry {
      Name = "metadata.ini",
      Size = metadata.Length,
      IsDirectory = false,
      Offset = 0,
      Data = metadata,
    });

    foreach (var vbn in this._fsInfoVbns)
      this._entries.Add(new WaflEntry {
        Name = $"fsinfo/vbn-{vbn}.bin",
        Size = BlockSize,
        IsDirectory = false,
        Offset = checked((long)vbn * BlockSize),
        Data = this.ReadBlock(vbn),
      });

    this._entries.Add(new WaflEntry {
      Name = "wafl-volume.bin",
      Size = this._imageSize,
      IsDirectory = false,
      Offset = 0,
      Data = [],
    });
  }

  private static VolInfoProbe? ProbeVolInfo(ReadOnlySpan<byte> block) {
    for (var offset = 0; offset <= block.Length - 8; offset += sizeof(uint)) {
      var word = block.Slice(offset, sizeof(uint));
      if (BinaryPrimitives.ReadUInt32BigEndian(word) == VolInfoMagic)
        return new VolInfoProbe(offset, false, BinaryPrimitives.ReadUInt32BigEndian(block.Slice(offset + 4, sizeof(uint))));
      if (BinaryPrimitives.ReadUInt32LittleEndian(word) == VolInfoMagic)
        return new VolInfoProbe(offset, true, BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(offset + 4, sizeof(uint))));
    }

    return null;
  }

  /// <summary>
  /// Discovers the classic direct fsinfo lookup table without assuming its byte
  /// offset. The fsinfo compatibility magic at the start of volinfo is used as a
  /// target-block invariant; only in-range 32-bit VBNs whose target starts with
  /// that same magic are candidates. A table start must be unique and its next
  /// entry must be either zero or another verified fsinfo reference.
  /// </summary>
  private FsInfoTableProbe? ProbeClassicFsInfoTable(ReadOnlySpan<byte> volInfoBlock, VolInfoProbe volInfo) {
    if (volInfoBlock.Length < 8) return null;

    var fsInfoMagic = ReadUInt32(volInfoBlock[..sizeof(uint)], volInfo.LittleEndian);
    var fsInfoVersion = ReadUInt32(volInfoBlock.Slice(sizeof(uint), sizeof(uint)), volInfo.LittleEndian);
    if (fsInfoMagic is 0 or uint.MaxValue || fsInfoMagic == VolInfoMagic)
      return null;

    var blockCount = this._imageSize / BlockSize;
    if (blockCount <= SecondVolInfoVbn + 1)
      return null;

    var cache = new Dictionary<uint, bool>();
    var references = new List<FsInfoReference>();
    var scanStart = AlignUp(volInfo.MagicOffset + 2 * sizeof(uint), ClassicVbnSize);

    for (var offset = scanStart; offset <= volInfoBlock.Length - ClassicVbnSize; offset += ClassicVbnSize) {
      var vbn = ReadUInt32(volInfoBlock.Slice(offset, ClassicVbnSize), volInfo.LittleEndian);
      if (vbn <= SecondVolInfoVbn || vbn >= blockCount)
        continue;
      if (!this.BlockStartsWithMagic(vbn, fsInfoMagic, volInfo.LittleEndian, cache))
        continue;
      references.Add(new FsInfoReference(offset, vbn));
    }

    if (references.Count == 0)
      return null;

    var verifiedOffsets = references.Select(reference => reference.Offset).ToHashSet();
    // `volInfoBlock` is a span, which the Where predicate cannot capture, so the filter runs as a
    // plain loop instead of a query.
    var starts = new List<FsInfoReference>();
    foreach (var reference in references)
      if (IsClassicTableStart(volInfoBlock, reference, verifiedOffsets, volInfo.LittleEndian))
        starts.Add(reference);

    if (starts.Count != 1)
      return new FsInfoTableProbe(fsInfoMagic, fsInfoVersion, null, -1, references, starts.Count > 1);

    var tableStart = starts[0];
    var tableEnd = Math.Min(volInfoBlock.Length, tableStart.Offset + MaxClassicFsInfoPointers * ClassicVbnSize);
    var tableReferences = references
      .Where(reference => reference.Offset >= tableStart.Offset && reference.Offset < tableEnd)
      .OrderBy(reference => reference.Offset)
      .ToList();

    return new FsInfoTableProbe(
      fsInfoMagic,
      fsInfoVersion,
      tableStart.Vbn,
      tableStart.Offset,
      tableReferences,
      Ambiguous: false);
  }

  private static bool IsClassicTableStart(
      ReadOnlySpan<byte> block,
      FsInfoReference reference,
      HashSet<int> verifiedOffsets,
      bool littleEndian) {
    if (verifiedOffsets.Contains(reference.Offset - ClassicVbnSize))
      return false;

    var nextOffset = reference.Offset + ClassicVbnSize;
    if (nextOffset > block.Length - ClassicVbnSize)
      return false;

    var next = ReadUInt32(block.Slice(nextOffset, ClassicVbnSize), littleEndian);
    return next == 0 || verifiedOffsets.Contains(nextOffset);
  }

  private bool BlockStartsWithMagic(uint vbn, uint magic, bool littleEndian, Dictionary<uint, bool> cache) {
    if (cache.TryGetValue(vbn, out var result))
      return result;

    Span<byte> prefix = stackalloc byte[sizeof(uint)];
    var saved = this._stream.Position;
    try {
      var offset = checked(this._origin + (long)vbn * BlockSize);
      if (offset < this._origin || offset > this._origin + this._imageSize - prefix.Length)
        return cache[vbn] = false;
      this._stream.Position = offset;
      this._stream.ReadExactly(prefix);
      result = ReadUInt32(prefix, littleEndian) == magic;
      cache[vbn] = result;
      return result;
    } finally {
      this._stream.Position = saved;
    }
  }

  private void PromoteStructuralStage() {
    var firstActive = this._firstFsInfoTable?.ActiveVbn;
    var secondActive = this._secondFsInfoTable?.ActiveVbn;

    if (firstActive is null && secondActive is null)
      return;

    this.Stage = 1;

    var vbns = new HashSet<uint>();
    AddReferences(this._firstFsInfoTable, vbns);
    AddReferences(this._secondFsInfoTable, vbns);
    this._fsInfoVbns.AddRange(vbns.Order());

    this.ActiveFsInfoVbn = (firstActive, secondActive) switch {
      ({ } first, { } second) when first == second => first,
      ({ } first, null) => first,
      (null, { } second) => second,
      _ => null,
    };
  }

  private static void AddReferences(FsInfoTableProbe? table, HashSet<uint> target) {
    if (table is not { ActiveVbn: not null }) return;
    foreach (var reference in table.References)
      target.Add(reference.Vbn);
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append(this.Stage > 0 ? "parse_status=structural-read-only\n" : "parse_status=detection-only\n");
    bldr.Append(CultureInfo.InvariantCulture, $"stage={this.Stage}\n");
    bldr.Append("format=NetApp WAFL logical volume\n");
    bldr.Append("volinfo_magic=0xdab8fbab\n");
    bldr.Append("volinfo_vbns=1,2\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_valid_copies={(this._firstVolInfo is not null ? 1 : 0) + (this._secondVolInfo is not null ? 1 : 0)}\n");
    AppendVolInfoProbe(bldr, FirstVolInfoVbn, this._firstVolInfo, this._firstFsInfoTable);
    AppendVolInfoProbe(bldr, SecondVolInfoVbn, this._secondVolInfo, this._secondFsInfoTable);
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_version={this.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={this._imageSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"allocation_block_size={BlockSize}\n");
    bldr.Append("structural_profile=classic-32bit-direct-fsinfo\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsinfo_reference_count={this._fsInfoVbns.Count}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"active_fsinfo_vbn={(this.ActiveFsInfoVbn is { } active ? active.ToString(CultureInfo.InvariantCulture) : "unknown")}\n");
    bldr.Append("maintenance_support=none\n");

    if (this.Stage > 0) {
      bldr.Append("note=Stage 1 — volinfo-to-fsinfo structural traversal succeeded for the disclosed classic 32-bit direct-fsinfo lookup-table profile. ");
      bldr.Append("Verified fsinfo blocks are surfaced under fsinfo/. This does not imply inode or namespace decoding. ");
      if (this.ActiveFsInfoVbn is null)
        bldr.Append("The redundant volinfo roots disagree, so no single active fsinfo root is asserted. ");
    } else {
      bldr.Append("note=Stage 0 — documented volinfo detection succeeded, but the classic direct-fsinfo lookup table could not be identified unambiguously. ");
    }

    bldr.Append("The input is treated as a flat logical VBN image, not as a physical ONTAP RAID member. ");
    bldr.Append("Full file walking and mutation still require version-specific inode layout, FBN/VBN/PVBN translation, FlexVol container mapping, ");
    bldr.Append("RAID member reconstruction, snapshot reachability, allocation maps and consistency-point/checksum commit semantics.\n");
    bldr.Append("upgrade_blockers=inode-layout,directory-layout,fbn-vbn-pvbn-translation,flexvol-container-map,raid-member-map,snapshot-reachability,allocation-maps,cp-checksums\n");
    bldr.Append("references=NetApp-ONTAP-EMS-raid.vol.volinfo.mismatch,US7313720,US5819292,US6289356,US7321962,Aaru-issue-61\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private static void AppendVolInfoProbe(
      StringBuilder bldr,
      int vbn,
      VolInfoProbe? probe,
      FsInfoTableProbe? fsInfoTable) {
    if (probe is not { } value) {
      bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}=invalid\n");
      return;
    }

    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}=valid\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_magic_offset={value.MagicOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_byte_order={(value.LittleEndian ? "little" : "big")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_version={value.Version}\n");

    if (fsInfoTable is not { } table) {
      bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_table=not-found\n");
      return;
    }

    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_magic=0x{table.FsInfoMagic:x8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_version_tag=0x{table.FsInfoVersion:x8}\n");
    if (table.Ambiguous) {
      bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_table=ambiguous\n");
      return;
    }
    if (table.ActiveVbn is not { } active) {
      bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_table=not-found\n");
      return;
    }

    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_table=verified\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_fsinfo_table_offset={table.TableOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_active_fsinfo_vbn={active}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_verified_fsinfo_references={table.References.Count}\n");
  }

  internal Stream OpenEntry(WaflEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.Name == "wafl-volume.bin") {
      this._stream.Position = this._origin;
      return new BoundedEntryStream(this._stream, this._imageSize, leaveOpen: true);
    }

    if (entry.Data.Length > 0 || entry.Size == 0)
      return new MemoryStream(entry.Data, writable: false);

    throw new FileNotFoundException($"WAFL entry not found: {entry.Name}", entry.Name);
  }

  /// <summary>
  /// Materializes an entry to memory. Large callers should use the descriptor's
  /// streaming <c>OpenEntry</c> API instead.
  /// </summary>
  public byte[] Extract(WaflEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Name != "wafl-volume.bin") return entry.Data;
    if ((ulong)entry.Size > (ulong)Array.MaxLength)
      throw new NotSupportedException("WAFL entry is too large for a byte array; use IArchiveFormatOperations.OpenEntry for streaming access.");

    using var source = this.OpenEntry(entry);
    var result = GC.AllocateUninitializedArray<byte>((int)entry.Size);
    source.ReadExactly(result);
    return result;
  }

  private byte[] ReadBlock(uint vbn) {
    var blockCount = this._imageSize / BlockSize;
    if (vbn >= blockCount)
      throw new InvalidDataException($"WAFL: VBN {vbn} lies outside the supplied logical image.");

    var result = GC.AllocateUninitializedArray<byte>(BlockSize);
    var saved = this._stream.Position;
    try {
      this._stream.Position = checked(this._origin + (long)vbn * BlockSize);
      this._stream.ReadExactly(result);
      return result;
    } finally {
      this._stream.Position = saved;
    }
  }

  private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
    => littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
      : BinaryPrimitives.ReadUInt32BigEndian(bytes);

  private static int AlignUp(int value, int alignment)
    => checked((value + alignment - 1) / alignment * alignment);

  /// <summary>Releases an internal compatibility buffer, if one was needed for a non-seekable source.</summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (this._ownsStream) this._stream.Dispose();
  }

  private readonly record struct VolInfoProbe(int MagicOffset, bool LittleEndian, uint Version);
  private readonly record struct FsInfoReference(int Offset, uint Vbn);
  private sealed record FsInfoTableProbe(
    uint FsInfoMagic,
    uint FsInfoVersion,
    uint? ActiveVbn,
    int TableOffset,
    IReadOnlyList<FsInfoReference> References,
    bool Ambiguous);
}
