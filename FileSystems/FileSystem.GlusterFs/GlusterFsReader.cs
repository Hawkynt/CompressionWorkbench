#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileSystem.Ext;
using FileSystem.Xfs;

namespace FileSystem.GlusterFs;

/// <summary>
/// Reads a single GlusterFS brick backing-store image by delegating the native
/// on-disk filesystem to the existing XFS or ext2/3/4 reader.
///
/// <para>GlusterFS does not define a separate block format. A brick is an export
/// directory on an ordinary filesystem that supports extended attributes. The
/// logical volume namespace and DHT/AFR/EC state span multiple bricks, while
/// object identity and translator metadata are stored in xattrs such as
/// <c>trusted.gfid</c> and <c>trusted.glusterfs.*</c>. Consequently this reader
/// intentionally exposes only the physical view of one supplied backing image.
/// It does not claim to reconstruct a Gluster volume from one brick.</para>
///
/// <para>When the backing namespace contains Gluster's <c>.glusterfs</c> GFID
/// index, its parent directory identifies the brick root. Entries outside that
/// subtree and the index itself are omitted from the normal view. If no index is
/// present, the filesystem root is used as a conservative explicit-input
/// fallback.</para>
///
/// <para>The backing readers currently do not interpret or mutate Gluster xattrs.
/// Those bytes remain untouched because this type is read-only.</para>
/// </summary>
public sealed class GlusterFsReader : IDisposable {

  private const int ExtSuperblockOffset = 1024;
  private const int ExtMagicOffset = ExtSuperblockOffset + 56;
  private const ushort ExtMagic = 0xEF53;
  private const string GlusterIndexName = ".glusterfs";

  private readonly Stream _image;
  private readonly bool _ownsImage;
  private readonly List<GlusterFsEntry> _entries = [];
  private XfsReader? _xfsReader;
  private ExtReader? _extReader;

  /// <summary>
  /// Gets the entries in the single-brick physical view.
  /// </summary>
  public IReadOnlyList<GlusterFsEntry> Entries => _entries;

  /// <summary>
  /// Gets a value indicating whether a supported backing filesystem was found.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Gets the detected backing filesystem name (<c>xfs</c> or <c>ext</c>).
  /// </summary>
  public string BackingFileSystem { get; private set; } = "";

  /// <summary>
  /// Gets the inferred brick root inside the backing filesystem. Empty means filesystem root.
  /// </summary>
  public string BrickRoot { get; private set; } = "";

  /// <summary>
  /// Gets whether a <c>.glusterfs</c> GFID index was present and used to locate the brick root.
  /// </summary>
  public bool HasGlusterIndex { get; private set; }

  /// <summary>
  /// Initializes a reader over one brick backing-store image.
  /// </summary>
  public GlusterFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("GlusterFS brick image must be readable.", nameof(stream));

    if (stream.CanSeek) {
      _image = stream;
    } else {
      var buffered = new MemoryStream();
      stream.CopyTo(buffered);
      buffered.Position = 0;
      _image = buffered;
      _ownsImage = true;
    }

    Parse();
  }

  private void Parse() {
    if (IsXfs()) {
      ParseXfs();
      return;
    }

    if (IsExt()) {
      ParseExt();
      return;
    }

    throw new InvalidDataException(
      "GlusterFS: input is not a supported brick backing-store image (expected XFS or ext2/3/4). " +
      "GlusterFS itself has no standalone image magic; use a backing-filesystem image of one brick.");
  }

  private bool IsXfs() {
    if (_image.Length < 4) return false;
    Span<byte> magic = stackalloc byte[4];
    ReadAt(0, magic);
    return magic.SequenceEqual("XFSB"u8);
  }

  private bool IsExt() {
    if (_image.Length < ExtMagicOffset + sizeof(ushort)) return false;
    Span<byte> magic = stackalloc byte[sizeof(ushort)];
    ReadAt(ExtMagicOffset, magic);
    return BinaryPrimitives.ReadUInt16LittleEndian(magic) == ExtMagic;
  }

  private void ParseXfs() {
    _image.Position = 0;
    _xfsReader = new XfsReader(_image, leaveOpen: true);
    this.BackingFileSystem = "xfs";
    this.ValidHeader = true;
    SetBrickRoot(_xfsReader.Entries.Select(entry => entry.Name));
    AddMetadata(_xfsReader.Entries.Count);

    foreach (var entry in _xfsReader.Entries) {
      if (!TryGetBrickRelativePath(entry.Name, out var relativePath)) continue;
      var captured = entry;
      _entries.Add(new GlusterFsEntry {
        Name = BrickPath(relativePath),
        Size = entry.Size,
        IsDirectory = entry.IsDirectory,
        DataFactory = entry.IsDirectory ? null : () => _xfsReader!.Extract(captured),
      });
    }
  }

  private void ParseExt() {
    _image.Position = 0;
    _extReader = new ExtReader(_image, leaveOpen: true);
    this.BackingFileSystem = "ext";
    this.ValidHeader = true;
    SetBrickRoot(_extReader.Entries.Select(entry => entry.Name));
    AddMetadata(_extReader.Entries.Count);

    foreach (var entry in _extReader.Entries) {
      if (!TryGetBrickRelativePath(entry.Name, out var relativePath)) continue;
      var captured = entry;
      _entries.Add(new GlusterFsEntry {
        Name = BrickPath(relativePath),
        Size = entry.Size,
        IsDirectory = entry.IsDirectory,
        DataFactory = entry.IsDirectory ? null : () => _extReader!.Extract(captured),
      });
    }
  }

  private void SetBrickRoot(IEnumerable<string> paths) {
    var roots = paths
      .Select(NormalizePath)
      .Where(IsGlusterIndexPath)
      .Select(path => path.Equals(GlusterIndexName, StringComparison.Ordinal)
        ? ""
        : path[..^(GlusterIndexName.Length + 1)])
      .Distinct(StringComparer.Ordinal)
      .ToArray();

    if (roots.Length > 1)
      throw new NotSupportedException(
        "GlusterFS: backing image contains multiple .glusterfs indexes; supply an image containing one brick.");

    this.HasGlusterIndex = roots.Length == 1;
    this.BrickRoot = roots.FirstOrDefault() ?? "";
  }

  private bool TryGetBrickRelativePath(string path, out string relativePath) {
    var normalized = NormalizePath(path);
    if (this.BrickRoot.Length != 0) {
      if (normalized.Equals(this.BrickRoot, StringComparison.Ordinal)) {
        relativePath = "";
        return false;
      }

      var prefix = this.BrickRoot + "/";
      if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) {
        relativePath = "";
        return false;
      }
      normalized = normalized[prefix.Length..];
    }

    if (normalized.Length == 0 || IsGlusterInternal(normalized)) {
      relativePath = "";
      return false;
    }

    relativePath = normalized;
    return true;
  }

  private void AddMetadata(int backingEntryCount) {
    var metadata = BuildMetadata(backingEntryCount);
    _entries.Add(new GlusterFsEntry {
      Name = "metadata.ini",
      Size = metadata.Length,
      Data = metadata,
    });
  }

  private byte[] BuildMetadata(int backingEntryCount) {
    var builder = new StringBuilder();
    builder.Append("parse_status=single-brick-read-only\n");
    builder.Append("format=GlusterFS brick backing store\n");
    builder.Append(CultureInfo.InvariantCulture, $"backing_fs={this.BackingFileSystem}\n");
    builder.Append(CultureInfo.InvariantCulture, $"image_size={_image.Length}\n");
    builder.Append(CultureInfo.InvariantCulture, $"backing_entry_count={backingEntryCount}\n");
    builder.Append(CultureInfo.InvariantCulture, $"brick_root={this.BrickRoot}\n");
    builder.Append(CultureInfo.InvariantCulture, $"gluster_index_detected={this.HasGlusterIndex.ToString().ToLowerInvariant()}\n");
    builder.Append("view=physical single-brick namespace\n");
    builder.Append("gluster_internal_directory=.glusterfs (hidden from normal listing)\n");
    builder.Append("xattrs=preserved in image but not interpreted\n");
    builder.Append("cluster_namespace_reconstruction=false\n");
    builder.Append("mutation=false\n");
    builder.Append("mutation_blocker=Gluster object identity and DHT/AFR/EC state live in trusted.gfid/trusted.glusterfs.* xattrs; current backing writers do not preserve those attributes during rebuilds.\n");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  private static string BrickPath(string path) => "brick/" + NormalizePath(path);

  private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

  private static bool IsGlusterIndexPath(string path) =>
    path.Equals(GlusterIndexName, StringComparison.Ordinal) ||
    path.EndsWith("/" + GlusterIndexName, StringComparison.Ordinal);

  private static bool IsGlusterInternal(string path) =>
    path.Equals(GlusterIndexName, StringComparison.Ordinal) ||
    path.StartsWith(GlusterIndexName + "/", StringComparison.Ordinal);

  private void ReadAt(long offset, Span<byte> destination) {
    var original = _image.Position;
    try {
      _image.Position = offset;
      _image.ReadExactly(destination);
    } finally {
      _image.Position = original;
    }
  }

  /// <summary>
  /// Extracts one surfaced entry.
  /// </summary>
  public byte[] Extract(GlusterFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    return entry.DataFactory?.Invoke() ?? entry.Data;
  }

  /// <summary>
  /// Releases backing filesystem readers and any spool created for a non-seekable input.
  /// </summary>
  public void Dispose() {
    _xfsReader?.Dispose();
    _extReader?.Dispose();
    if (_ownsImage) _image.Dispose();
  }
}
