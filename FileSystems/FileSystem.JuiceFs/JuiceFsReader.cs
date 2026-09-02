#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.JuiceFs;

/// <summary>
/// Stage 0 detection-only reader for JuiceFS artefacts.
/// JuiceFS is a POSIX-compatible distributed FS with NO standalone
/// on-disk image format: a volume is the combination of an external
/// metadata engine (Redis / MySQL / TiKV / SQLite / PostgreSQL / etcd /
/// FoundationDB / BadgerDB) and chunks living in S3-compatible object
/// storage (S3 / GCS / MinIO / OSS / OBS / …).
///
/// Real artefacts in the wild:
/// <list type="bullet">
///   <item><c>juicefs dump</c> JSON: a plain JSON document starting with <c>{"Setting":{</c>;
///     no offset-0 magic.</item>
///   <item><c>juicefs dump --binary</c> (v1.3+): protobuf segments, ends with a 4-byte
///     big-endian BakEOS marker <c>0x00747083</c> followed by a protobuf
///     <c>pb.Footer</c> and an 8-byte big-endian footer-length trailer
///     (juicedata/juicefs <c>pkg/meta/backup.go</c>, <c>BakMagic = 0x747083</c>).</item>
///   <item>SQLite metadata backend: a standard SQLite database
///     (<c>"SQLite format 3\0"</c>) containing <c>jfs_node</c>, <c>jfs_edge</c>,
///     <c>jfs_chunk</c>, <c>jfs_setting</c> tables. Filesystem listing without object-store
///     access would still extract zero bytes for every file.</item>
/// </list>
///
/// This reader recognises a wrapper-convention tag (ASCII <c>"JuiceFS"</c> at
/// offset 0) for surfacing detection only — real JuiceFS files do NOT carry
/// that tag. Even if they did, R/O extraction would still be impossible
/// because (a) inode → chunk-id resolution lives in the metadata engine and
/// (b) chunk bytes live behind an object-store endpoint. Returning empty /
/// zero bytes from <c>Extract()</c> would be dishonest; instead we surface
/// the raw image and a self-describing <c>metadata.ini</c> that explains why
/// real extraction is structurally impossible.
/// </summary>
public sealed class JuiceFsReader : IDisposable {

  /// <summary>
  /// Wrapper-convention detection tag: ASCII "JuiceFS" (7 bytes).
  /// NOTE: this is NOT a real JuiceFS signature. Real binary backups
  /// store BakMagic 0x00747083 (4 bytes BE) in the EOS marker + protobuf
  /// footer at end-of-file; JSON dumps start with '{'; the SQLite backend
  /// is a standard SQLite database.
  /// </summary>
  public static readonly byte[] DumpTag = "JuiceFS"u8.ToArray();

  /// <summary>
  /// Real JuiceFS binary-backup magic (BakMagic, juicefs 1.3+). Stored
  /// big-endian as the BakEOS marker just before the protobuf footer.
  /// Source: juicedata/juicefs pkg/meta/backup.go.
  /// </summary>
  public const uint BakMagic = 0x00747083u;

  private readonly byte[] _data;
  private readonly List<JuiceFsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<JuiceFsEntry> Entries => _entries;
  /// <summary>
  /// Gets or sets the trailing word.
  /// </summary>
public uint TrailingWord { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
public bool ValidHeader { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="JuiceFsReader"/>.
  /// </summary>
public JuiceFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("JuiceFS: file too small for wrapper-convention tag.");

    if (!_data.AsSpan(0, 7).SequenceEqual(DumpTag))
      throw new InvalidDataException("JuiceFS: missing 'JuiceFS' wrapper tag at offset 0.");

    if (_data.Length >= 12)
      this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8, 4));
    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new JuiceFsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new JuiceFsEntry { Name = "juicefs-bundle.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("format=JuiceFS distributed FS (no standalone on-disk image)\n");
    bldr.Append("wrapper_tag=JuiceFS\n");
    bldr.Append("wrapper_tag_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("real_binary_backup_magic=0x00747083 (BakMagic, 4 bytes BE, EOS marker + protobuf footer at end-of-file)\n");
    bldr.Append("real_json_dump=plain JSON, starts with '{'\n");
    bldr.Append("real_sqlite_backend=standard SQLite database (jfs_node/jfs_edge/jfs_chunk/jfs_setting tables)\n");
    bldr.Append("metadata_engines=Redis | MySQL | PostgreSQL | TiKV | SQLite | etcd | FoundationDB | BadgerDB\n");
    bldr.Append("object_stores=S3 | GCS | MinIO | OSS | OBS | Swift | Azure Blob | …\n");
    bldr.Append("ro_extraction_impossible_reason=inode->chunk-id resolution lives in the metadata engine; ");
    bldr.Append("chunk bytes live behind an object-store endpoint. Neither is reachable from a single local file. ");
    bldr.Append("Even when reading the SQLite metadata backend, file bodies would still extract to zero bytes ");
    bldr.Append("without an object-store connection.\n");
    bldr.Append("treatment=Stage 0 confirmed (no standalone on-disk format).\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(JuiceFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
