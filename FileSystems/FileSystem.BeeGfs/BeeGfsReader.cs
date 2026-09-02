#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.BeeGfs;

/// <summary>
/// Stage 0 detection-only reader for BeeGFS chunk-file / dump tags.
/// BeeGFS (Fraunhofer Parallel Cluster FS, originally FhGFS) is a
/// distributed parallel cluster filesystem. There is <b>no standalone
/// on-disk image format</b> for a BeeGFS volume: the namespace lives
/// across one or more metadata targets (each a directory tree on a
/// regular Linux FS like ext4/xfs, with per-inode metadata stored as
/// files + extended attributes), and the file payload lives across
/// many storage targets (chunk files in a 2-level hash directory
/// layout on the storage targets' regular Linux FS). Reconstructing
/// a single logical file requires the live metadata-server stripe
/// pattern + storage-target map; a single byte-stream cannot represent
/// it.
///
/// This descriptor therefore only verifies the ASCII tag
/// <c>"BeeGFS"</c> (6 bytes, 0x42 0x65 0x65 0x47 0x46 0x53) or the
/// short 4-byte tag <c>"BeeG"</c> (0x42 0x65 0x65 0x47 = 0x42656547
/// BE) at offset 0 of a chunk-file or dump produced by a BeeGFS
/// utility, and surfaces a synthetic <c>metadata.ini</c> documenting
/// the tag + a raw <c>beegfs-chunk.bin</c> blob containing the file
/// bytes verbatim. Promotion to R/O is not possible from a single
/// stream — see <c>Description</c> on the descriptor.
/// </summary>
public sealed class BeeGfsReader : IDisposable {

  /// <summary>BeeGFS long tag: ASCII "BeeGFS" (6 bytes).</summary>
  public static readonly byte[] LongTag = "BeeGFS"u8.ToArray();
  /// <summary>BeeGFS short tag: ASCII "BeeG" (4 bytes, 0x42656547 BE).</summary>
  public static readonly byte[] ShortTag = "BeeG"u8.ToArray();

  private readonly byte[] _data;
  private readonly List<BeeGfsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<BeeGfsEntry> Entries => _entries;
  /// <summary>
  /// Gets or sets the tag.
  /// </summary>
  public string Tag { get; private set; } = "";
  /// <summary>
  /// Gets or sets the trailing word.
  /// </summary>
  public uint TrailingWord { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="BeeGfsReader"/>.
  /// </summary>
  public BeeGfsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("BeeGFS: file too small for chunk header.");

    if (_data.AsSpan(0, 6).SequenceEqual(LongTag)) {
      this.Tag = "BeeGFS";
      this.TrailingWord = _data.Length >= 12
        ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8, 4)) : 0;
    } else if (_data.AsSpan(0, 4).SequenceEqual(ShortTag)) {
      this.Tag = "BeeG";
      this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    } else {
      throw new InvalidDataException("BeeGFS: missing 'BeeGFS' / 'BeeG' tag at offset 0.");
    }

    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new BeeGfsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new BeeGfsEntry { Name = "beegfs-chunk.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("format=BeeGFS chunk-file / dump tag\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_tag={this.Tag}\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("note=Stage 0 — detection only. BeeGFS has no standalone on-disk image: ");
    bldr.Append("a volume is a logical view across metadata-target processes (per-inode files + xattrs ");
    bldr.Append("on a regular Linux FS) and storage-target processes (chunk files in a hashed dir layout ");
    bldr.Append("on a regular Linux FS). Reconstructing files requires the live stripe pattern + ");
    bldr.Append("target group map from beegfs-meta. R/O promotion from a single stream is not possible.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(BeeGfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
