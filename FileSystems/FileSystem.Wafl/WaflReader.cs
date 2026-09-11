#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry.Streaming;

namespace FileSystem.Wafl;

/// <summary>
/// Stage-0 reader for a flat logical NetApp WAFL volume image.
///
/// <para>
/// NetApp documents the WAFL volinfo superblock at volume block numbers 1 and 2
/// and identifies its magic as <c>0xdab8fbab</c>. The exact byte offset of the
/// volinfo-magic field inside every ONTAP generation is not published as a stable
/// ABI, so this reader validates the two 4 KiB volinfo blocks by locating that
/// aligned 32-bit value rather than inventing a fixed field offset.
/// </para>
///
/// <para>
/// This is deliberately not a filesystem walker. Aggregate/FlexVol translation,
/// RAID member placement, allocation maps and snapshot reachability are required
/// before user data or free space can be interpreted safely.
/// </para>
/// </summary>
public sealed class WaflReader : IDisposable {

  /// <summary>
  /// Legacy public alias retained for API compatibility. It now contains the
  /// documented volinfo magic in big-endian byte order; the former ASCII
  /// <c>"wafd"</c> value was not a published WAFL signature.
  /// </summary>
  [Obsolete("Use WAFL volinfo validation through WaflReader; the magic has no stable fixed byte offset across all ONTAP generations.")]
  public static readonly byte[] FsInfoTag = [0xDA, 0xB8, 0xFB, 0xAB];

  private const int BlockSize = 4096;
  private const int FirstVolInfoVbn = 1;
  private const int SecondVolInfoVbn = 2;
  private const uint VolInfoMagic = 0xDAB8FBAB;
  private const int MinimumImageSize = (SecondVolInfoVbn + 1) * BlockSize;

  private readonly Stream _stream;
  private readonly bool _ownsStream;
  private readonly long _origin;
  private readonly long _imageSize;
  private readonly List<WaflEntry> _entries = [];
  private bool _disposed;
  private VolInfoProbe? _firstVolInfo;
  private VolInfoProbe? _secondVolInfo;

  /// <summary>Gets the synthetic entries exposed by this Stage-0 reader.</summary>
  public IReadOnlyList<WaflEntry> Entries => this._entries;

  /// <summary>
  /// Gets the volinfo version from the first valid superblock copy. When VBN 1
  /// is damaged, the value is taken from VBN 2.
  /// </summary>
  public uint Version { get; private set; }

  /// <summary>Gets a value indicating whether at least one documented volinfo superblock was recognized.</summary>
  public bool ValidHeader { get; private set; }

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

    this._firstVolInfo = ProbeVolInfo(copies.AsSpan(0, BlockSize));
    this._secondVolInfo = ProbeVolInfo(copies.AsSpan(BlockSize, BlockSize));

    var selected = this._firstVolInfo ?? this._secondVolInfo;
    if (selected is null)
      throw new InvalidDataException("WAFL: neither VBN 1 nor VBN 2 contains the documented volinfo magic 0xdab8fbab.");

    this.Version = selected.Value.Version;
    this.ValidHeader = true;

    var metadata = this.BuildMetadata();
    this._entries.Add(new WaflEntry {
      Name = "metadata.ini",
      Size = metadata.Length,
      IsDirectory = false,
      Offset = 0,
      Data = metadata,
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

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("format=NetApp WAFL logical volume\n");
    bldr.Append("volinfo_magic=0xdab8fbab\n");
    bldr.Append("volinfo_vbns=1,2\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_valid_copies={(this._firstVolInfo is not null ? 1 : 0) + (this._secondVolInfo is not null ? 1 : 0)}\n");
    AppendProbe(bldr, FirstVolInfoVbn, this._firstVolInfo);
    AppendProbe(bldr, SecondVolInfoVbn, this._secondVolInfo);
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_version={this.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={this._imageSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"allocation_block_size={BlockSize}\n");
    bldr.Append("maintenance_support=none\n");
    bldr.Append("note=Stage 0 (confirmed) — documented volinfo detection plus opaque streaming only. ");
    bldr.Append("The input is treated as a flat logical VBN image, not as a physical ONTAP RAID member. ");
    bldr.Append("File content and free-space reachability require FBN/VBN/PVBN translation, FlexVol container mapping, ");
    bldr.Append("RAID member/stripe reconstruction, snapshot reachability, and consistency-point mutation semantics that are ");
    bldr.Append("not published at the byte-level needed for a safe offline writer.\n");
    bldr.Append("upgrade_blockers=fbn-vbn-pvbn-translation,flexvol-container-map,raid-member-map,snapshot-reachability,cp-format\n");
    bldr.Append("references=NetApp-ONTAP-EMS-raid.vol.volinfo.mismatch,US7313720,US5819292,US6289356,Aaru-issue-61\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private static void AppendProbe(StringBuilder bldr, int vbn, VolInfoProbe? probe) {
    if (probe is not { } value) {
      bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}=invalid\n");
      return;
    }

    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}=valid\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_magic_offset={value.MagicOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_byte_order={(value.LittleEndian ? "little" : "big")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volinfo_vbn{vbn}_version={value.Version}\n");
  }

  internal Stream OpenEntry(WaflEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.Name == "metadata.ini")
      return new MemoryStream(entry.Data, writable: false);

    if (entry.Name != "wafl-volume.bin")
      throw new FileNotFoundException($"WAFL entry not found: {entry.Name}", entry.Name);

    this._stream.Position = this._origin;
    return new BoundedEntryStream(this._stream, this._imageSize, leaveOpen: true);
  }

  /// <summary>
  /// Materializes an entry to memory. Large callers should use the descriptor's
  /// streaming <c>OpenEntry</c> API instead.
  /// </summary>
  public byte[] Extract(WaflEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Name == "metadata.ini") return entry.Data;
    if ((ulong)entry.Size > (ulong)Array.MaxLength)
      throw new NotSupportedException("WAFL entry is too large for a byte array; use IArchiveFormatOperations.OpenEntry for streaming access.");

    using var source = this.OpenEntry(entry);
    var result = GC.AllocateUninitializedArray<byte>((int)entry.Size);
    source.ReadExactly(result);
    return result;
  }

  /// <summary>Releases an internal compatibility buffer, if one was needed for a non-seekable source.</summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (this._ownsStream) this._stream.Dispose();
  }

  private readonly record struct VolInfoProbe(int MagicOffset, bool LittleEndian, uint Version);
}
