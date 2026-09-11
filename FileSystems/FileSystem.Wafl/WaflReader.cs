#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry.Streaming;

namespace FileSystem.Wafl;

/// <summary>
/// Stage 0 reader for NetApp WAFL (Write-Anywhere File Layout) volume images.
///
/// <para>
/// WAFL is NetApp's proprietary cluster/NAS filesystem. The public material is
/// sufficient to identify the FSinfo prefix and the fixed 4 KiB allocation unit,
/// but not to walk a modern ONTAP aggregate/FlexVol safely from a standalone
/// image. The reader therefore exposes a small synthetic metadata document and a
/// bounded stream over the opaque source image rather than pretending that the
/// inode tree is understood.
/// </para>
///
/// <para>
/// Seekable inputs are never copied wholesale: only the eight-byte Stage-0
/// header is read during construction. Non-seekable streams retain the historical
/// compatibility fallback and are buffered because random access is required for
/// the raw-image pseudo-entry.
/// </para>
/// </summary>
public sealed class WaflReader : IDisposable {

  /// <summary>WAFL FSinfo tag bytes: ASCII "wafd" = 0x77 0x61 0x66 0x64.</summary>
  public static readonly byte[] FsInfoTag = "wafd"u8.ToArray();

  /// <summary>The WAFL allocation block size documented by NetApp's public papers and patents.</summary>
  public const int BlockSize = 4096;

  private const int HeaderSize = 8;

  private readonly Stream _stream;
  private readonly bool _ownsStream;
  private readonly long _origin;
  private readonly long _imageSize;
  private readonly List<WaflEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets the synthetic entries exposed by this Stage-0 reader.</summary>
  public IReadOnlyList<WaflEntry> Entries => this._entries;

  /// <summary>Gets the big-endian Stage-0 FSinfo version field.</summary>
  public uint Version { get; private set; }

  /// <summary>Gets a value indicating whether the Stage-0 header is valid.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>Gets the size in bytes of the image region represented by this reader.</summary>
  public long ImageSize => this._imageSize;

  /// <summary>
  /// Initializes a new instance of <see cref="WaflReader"/>.
  /// </summary>
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
    if (this._imageSize < HeaderSize)
      throw new InvalidDataException("WAFL: file too small for FSinfo header.");

    Span<byte> header = stackalloc byte[HeaderSize];
    var saved = this._stream.Position;
    try {
      this._stream.Position = this._origin;
      this._stream.ReadExactly(header);
    } finally {
      this._stream.Position = saved;
    }

    if (!header[..4].SequenceEqual(FsInfoTag))
      throw new InvalidDataException("WAFL: missing 'wafd' tag at the image origin.");

    this.Version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
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

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("format=NetApp WAFL volume\n");
    bldr.Append("magic_tag=wafd\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsinfo_version={this.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={this._imageSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"allocation_block_size={BlockSize}\n");
    bldr.Append("layout_analysis=fixed-4k-block-size-only\n");
    bldr.Append("maintenance_support=none\n");
    bldr.Append("note=Stage 0 (confirmed) — detection plus opaque streaming only. WAFL is a proprietary ONTAP filesystem; ");
    bldr.Append("single-image content traversal requires the NetApp volume manager. ");
    bldr.Append("File content and free-space reachability require FBN/VBN/PVBN translation, FlexVol container mapping, ");
    bldr.Append("RAID member/stripe reconstruction, snapshot reachability, and consistency-point semantics that are not ");
    bldr.Append("published at the byte-level needed for a safe offline writer.\n");
    bldr.Append("upgrade_blockers=fbn-vbn-pvbn-translation,flexvol-container-map,raid-member-map,snapshot-reachability,cp-format\n");
    bldr.Append("references=Hitz1994-TR3002,US5819292,US6289356,Aaru-issue-61\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Opens one synthetic entry without materializing the whole image.
  /// </summary>
  public Stream OpenEntry(WaflEntry entry) {
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
  /// Materializes an entry to memory. Large callers should prefer <see cref="OpenEntry"/>.
  /// </summary>
  public byte[] Extract(WaflEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Name == "metadata.ini") return entry.Data;
    if ((ulong)entry.Size > (ulong)Array.MaxLength)
      throw new NotSupportedException("WAFL entry is too large for a byte array; use OpenEntry for streaming access.");

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
}
