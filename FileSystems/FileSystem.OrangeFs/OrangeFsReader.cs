#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.OrangeFs;

/// <summary>
/// Reads OrangeFS / PVFS2 DBPF storage-object files. PVFS2 (the parallel
/// virtual filesystem, now OrangeFS) is a distributed parallel FS, but
/// its server-side storage objects are persisted in single files named
/// like <c>bstream-XX</c> using the Direct Block Pool Format (DBPF). Each
/// such file has a 16-byte header with a 4-byte ASCII tag at offset 0:
/// <c>"PVFS"</c> (0x50 0x56 0x46 0x53) for classic PVFS2 or <c>"OGFP"</c>
/// (0x4F 0x47 0x46 0x50) for OrangeFS-native objects, followed by a
/// version field and a datastream type byte.
///
/// DBPF header layout (file offset 0, little-endian):
///   0x00 char[4] tag                  "PVFS" or "OGFP"
///   0x04 u32     version              (DBPF format revision)
///   0x08 u32     datastream-type      (bytestream / metadata / dirdata / ...)
///   0x0C u32     object-size          (length of contained object payload)
///   0x10 ...     object data
///
/// The contained object is surfaced as a single opaque entry — full PVFS2
/// object semantics (handle/fsid resolution + striping) require the cluster's
/// config (fs.conf) and are out of scope.
/// </summary>
public sealed class OrangeFsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<OrangeFsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<OrangeFsEntry> Entries => _entries;

  /// <summary>
  /// Gets or sets the tag.
  /// </summary>
public string Tag { get; private set; } = "";
  /// <summary>
  /// Gets or sets the version.
  /// </summary>
public uint Version { get; private set; }
  /// <summary>
  /// Gets or sets the datastream type.
  /// </summary>
public uint DatastreamType { get; private set; }
  /// <summary>
  /// Gets or sets the object size.
  /// </summary>
public uint ObjectSize { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
public bool ValidHeader { get; private set; }
  /// <summary>
  /// Gets a value indicating whether is orange fs.
  /// </summary>
public bool IsOrangeFs { get; private set; }

  /// <summary>
  /// Provides the pvfs tag value.
  /// </summary>
public static readonly byte[] PvfsTag = "PVFS"u8.ToArray();
  /// <summary>
  /// Provides the orange fs tag value.
  /// </summary>
public static readonly byte[] OrangeFsTag = "OGFP"u8.ToArray();
  private const int HeaderSize = 16;

  /// <summary>
  /// Initializes a new instance of <see cref="OrangeFsReader"/>.
  /// </summary>
public OrangeFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("OrangeFs: file too small for DBPF header.");

    var head = _data.AsSpan(0, 4);
    if (head.SequenceEqual(PvfsTag)) {
      this.Tag = "PVFS";
      this.IsOrangeFs = false;
    } else if (head.SequenceEqual(OrangeFsTag)) {
      this.Tag = "OGFP";
      this.IsOrangeFs = true;
    } else {
      throw new InvalidDataException("OrangeFs: header missing PVFS/OGFP tag at offset 0.");
    }

    this.Version        = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(4, 4));
    this.DatastreamType = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(8, 4));
    this.ObjectSize     = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(12, 4));
    this.ValidHeader = true;

    var meta = BuildMetadata();
    var ext = this.IsOrangeFs ? ".orangefs" : ".pvfs";
    _entries.Add(new OrangeFsEntry { Name = $"FULL{ext}", Size = _data.Length, IsDirectory = false, Data = _data });
    _entries.Add(new OrangeFsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Data = meta });

    var payloadLen = (int)Math.Min((long)this.ObjectSize, _data.Length - HeaderSize);
    if (payloadLen > 0) {
      var blob = _data.AsSpan(HeaderSize, payloadLen).ToArray();
      _entries.Add(new OrangeFsEntry { Name = "object.bin", Size = blob.Length, IsDirectory = false, Data = blob });
    }
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=").Append(this.IsOrangeFs ? "OrangeFS DBPF" : "PVFS2 DBPF").Append('\n');
    bldr.Append(CultureInfo.InvariantCulture, $"tag={this.Tag}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={this.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"datastream_type={this.DatastreamType}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"object_size={this.ObjectSize}\n");
    bldr.Append("note=Single DBPF storage object surfaced opaque; cluster fs.conf required for semantic resolution.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(OrangeFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
