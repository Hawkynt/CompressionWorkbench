using System.Buffers.Binary;
using System.Text;

namespace FileFormat.StuffIt;

/// <summary>
/// Creates StuffIt 5 archives (the container of StuffIt 5 to 8): files and folders, each fork
/// stored, RLE-packed, LZ+Huffman-compressed (method 13) or Arsenic-compressed (method 15), according to the chosen compression setting.
/// </summary>
/// <remarks>
/// <para>
/// Layout, as StuffIt 7 for Windows writes it:
/// <list type="bullet">
///   <item>A 100-byte archive header: 80 bytes of signature text, <c>1A 00</c>, version 5, flags
///   0, the total archive size, the offset of the first entry (twice, around the root entry
///   count), and a CRC-16/ARC over the 100 bytes with its own field zeroed.</item>
///   <item>Per file, an entry header (version 3) of 48 bytes plus the name. It holds the
///   <c>A5A5A5A5</c> marker, the header size, flags (0x40 = folder), the creation and
///   modification dates, the offsets of the previous and next entries and of the parent folder
///   (0 at the root), the name length, the header CRC, the data fork's size, compressed size,
///   CRC-16 and method.</item>
///   <item>A 32-byte second block: fork flags (bit 0 = resource fork present), its own CRC-16,
///   type, creator and Finder flags.</item>
///   <item>An optional 14-byte resource fork record.</item>
///   <item>Then the resource fork data, followed by the data fork data.</item>
/// </list>
/// </para>
/// <para>
/// A folder entry has the same header and second block. In place of the fork fields it holds the
/// offset of its first child, the total size of the files inside it, the byte length of its
/// contents and its child count. Its children follow it, and then a 48-byte end record: a folder
/// header with no name, a data size of <c>FFFFFFFF</c> and no second block. Siblings are chained
/// through the previous and next offsets. A folder's first child points back at the folder, its
/// last child points on to the end record, and the end record points back at the last child.
/// </para>
/// <para>
/// Both CRCs are CRC-16/ARC: the header CRC covers the header with its own field zeroed, and the
/// second block's CRC covers that block plus the resource record, again with its own field zeroed.
/// Both rules, and the folder layout, were measured on archives StuffIt wrote.
/// </para>
/// </remarks>
///
/// With a password the archive is encrypted the way StuffIt 5 encrypts it: archive flag 0x80 and,
/// right after the 100-byte header, a length byte (5) and the first 5 bytes of MD5(archive key),
/// where the archive key is the first 5 bytes of MD5(password). Every entry is flagged 0x20 (folders
/// 0x60). Each non-empty fork gets a random 5-byte key, stored after its method/key-length bytes
/// (data fork: before the name; resource fork: after its record), and its compressed bytes are
/// RC4-encrypted under archive key ‖ fork key. CRCs stay those of the plain fork data.
public sealed class StuffIt5Writer : IDisposable {
  private const string SignatureText = "StuffIt (c)1997-2001 Aladdin Systems, Inc., http://www.aladdinsys.com/StuffIt/\r\n";
  private const int ArchiveHeaderSize = 100;
  private const int EntryFixedSize = 48;
  private const int SecondBlockSize = 32;
  private const int ResourceRecordSize = 14;
  private const int EndRecordSize = 48;
  private const uint EntryMarker = 0xA5A5A5A5;
  private const byte FlagFolder = 0x40;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly StuffItTree<Pending> _tree = new();
  private readonly StuffItCompression _compression;
  private readonly byte[]? _archiveKey;
  private bool _disposed;

  private int KeyLength => this._archiveKey == null ? 0 : StuffIt5Crypto.KeyLength;

  private sealed record Pending(string Type, string Creator, byte[] Data, byte[] Resource,
    byte[] PackedData, byte DataMethod, byte[] PackedResource, byte ResourceMethod);

  private sealed class Placed(StuffItTree<Pending>.Node node, byte[] name) {
    public StuffItTree<Pending>.Node Node { get; } = node;
    public byte[] Name { get; } = name;
    public long Offset;
    public long Size;
    public long EndRecord; // folders: offset of the end record
    public long FilesSize; // folders: total size of the files inside
    public List<Placed> Children { get; } = [];
  }

  /// <summary>Starts a StuffIt 5 archive on <paramref name="stream"/>; it is written on <see cref="Dispose"/>.</summary>
  public StuffIt5Writer(Stream stream, bool leaveOpen = false) : this(stream, StuffItCompression.Smallest, leaveOpen) { }

  /// <summary>Starts a StuffIt 5 archive whose forks are compressed as <paramref name="compression"/> asks.</summary>
  public StuffIt5Writer(Stream stream, StuffItCompression compression, bool leaveOpen = false) : this(stream, compression, null, leaveOpen) { }

  /// <summary>Starts a StuffIt 5 archive whose forks are compressed as <paramref name="compression"/> asks and, with a <paramref name="password"/>, encrypted.</summary>
  public StuffIt5Writer(Stream stream, StuffItCompression compression, string? password, bool leaveOpen = false) {
    this._compression = compression;
    if (!string.IsNullOrEmpty(password)) this._archiveKey = StuffIt5Crypto.KeyOf(password);
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file with a data fork and an optional resource fork; '/' in the path creates folders.</summary>
  /// <exception cref="NotSupportedException">A name is empty, longer than 255 bytes, or not representable in Latin-1.</exception>
  public void AddFile(string fileName, byte[] data, byte[]? resourceFork = null, string fileType = "TEXT", string fileCreator = "CWIE", DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    CheckPath(fileName);
    resourceFork ??= [];
    var packedData = StuffItWriter.CompressFork(data, out var dataMethod, this._compression);
    var packedResource = StuffItWriter.CompressFork(resourceFork, out var resourceMethod, this._compression);
    this._tree.AddFile(fileName, new Pending(fileType, fileCreator, data, resourceFork, packedData, dataMethod, packedResource, resourceMethod),
      lastModified ?? DateTime.UtcNow);
  }

  /// <summary>Adds a folder, which may stay empty; folders named in file paths are created on their own.</summary>
  public void AddDirectory(string path, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(path);
    CheckPath(path);
    this._tree.AddFolder(path, lastModified ?? DateTime.UtcNow);
  }

  private static void CheckPath(string path) {
    foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      var name = Encoding.Latin1.GetBytes(part);
      if (name.Length is 0 or > 255 || Encoding.Latin1.GetString(name) != part)
        throw new NotSupportedException($"A StuffIt 5 name holds 1-255 Latin-1 bytes; '{part}' does not fit.");
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this.Write();
    if (!this._leaveOpen) this._stream.Dispose();
  }

  // ── layout ───────────────────────────────────────────────────────────────

  private static List<Placed> Place(IEnumerable<StuffItTree<Pending>.Node> nodes) => nodes.Select(n => {
    var placed = new Placed(n, Encoding.Latin1.GetBytes(n.Name));
    placed.Children.AddRange(Place(n.Children));
    return placed;
  }).ToList();

  /// <summary>Assigns offsets depth-first from <paramref name="position"/>; returns the position after the last entry.</summary>
  private long Assign(List<Placed> entries, long position) {
    foreach (var e in entries) {
      e.Offset = position;
      position += EntryFixedSize + e.Name.Length + SecondBlockSize;
      if (e.Node.File is { } f) {
        position += (f.Data.Length > 0 ? this.KeyLength : 0)
          + (f.Resource.Length > 0 ? ResourceRecordSize + this.KeyLength : 0) + f.PackedResource.Length + f.PackedData.Length;
      } else {
        position = this.Assign(e.Children, position);
        e.EndRecord = position;
        position += EndRecordSize;
        e.FilesSize = e.Children.Sum(c => c.Node.File is { } cf ? (long)cf.Data.Length + cf.Resource.Length : c.FilesSize);
      }
      e.Size = position - e.Offset;
    }
    return position;
  }

  private void Write() {
    var root = Place(this._tree.Root.Children);
    var first = ArchiveHeaderSize + (this._archiveKey == null ? 0 : 1 + StuffIt5Crypto.KeyLength);
    var end = this.Assign(root, first);
    if (end > uint.MaxValue) throw new NotSupportedException("A StuffIt 5 archive is limited to 4 GiB.");

    var header = new byte[ArchiveHeaderSize];
    Encoding.ASCII.GetBytes(SignatureText).CopyTo(header, 0);
    header[80] = 0x1A;
    header[82] = 5;
    if (this._archiveKey != null) header[83] = StuffIt5Crypto.ArchiveFlagEncrypted;
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(84), (uint)end);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(88), (uint)first);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(92), (ushort)root.Count);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(94), (uint)first);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(98), StuffItWriter.ComputeCrc16(header));
    this._stream.Write(header);
    if (this._archiveKey != null) {
      this._stream.WriteByte(StuffIt5Crypto.KeyLength);
      this._stream.Write(StuffIt5Crypto.CheckValue(this._archiveKey));
    }

    this.WriteLevel(root, parent: 0, firstPrevious: 0, lastNext: 0);
    this._stream.Flush();
  }

  private void WriteLevel(List<Placed> entries, long parent, long firstPrevious, long lastNext) {
    for (var i = 0; i < entries.Count; ++i) {
      var previous = i > 0 ? entries[i - 1].Offset : firstPrevious;
      var next = i + 1 < entries.Count ? entries[i + 1].Offset : lastNext;
      var e = entries[i];
      if (e.Node.File is { } file) {
        this.WriteFile(e, file, parent, previous, next);
        continue;
      }
      this.WriteFolder(e, parent, previous, next);
      this.WriteLevel(e.Children, e.Offset, e.Offset, e.EndRecord);
      this.WriteEndRecord(e.Children.Count > 0 ? e.Children[^1].Offset : e.Offset, e.Offset);
    }
  }

  private static byte[] EntryHeader(byte[] name, byte flags, uint date, long parent, long previous, long next, byte[]? key = null) {
    var keyLength = key?.Length ?? 0;
    var h = new byte[EntryFixedSize + keyLength + name.Length];
    BinaryPrimitives.WriteUInt32BigEndian(h, EntryMarker);
    h[4] = 3; // entry version
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(6), (ushort)h.Length);
    h[9] = flags;
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(10), date);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(14), date);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(18), (uint)previous);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(22), (uint)next);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(26), (uint)parent);
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(30), (ushort)name.Length);
    if (key != null) {
      h[47] = (byte)keyLength;
      key.CopyTo(h, EntryFixedSize);
    }
    name.CopyTo(h, EntryFixedSize + keyLength);
    return h;
  }

  private static void SealHeader(byte[] h) => BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(32), StuffItWriter.ComputeCrc16(h));

  private void WriteFile(Placed e, Pending f, long parent, long previous, long next) {
    var date = StuffItWriter.ToMacTimestamp(e.Node.Modified);
    var hasResource = f.Resource.Length > 0;
    var encrypted = this._archiveKey != null;
    var dataKey = encrypted && f.Data.Length > 0 ? NewKey() : null;
    var resourceKey = encrypted && hasResource ? NewKey() : null;
    var h = EntryHeader(e.Name, encrypted ? StuffIt5Crypto.EntryFlagEncrypted : (byte)0, date, parent, previous, next, dataKey);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(34), (uint)f.Data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(38), (uint)f.PackedData.Length);
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(42), StuffItWriter.ComputeCrc16(f.Data));
    h[46] = f.DataMethod;
    // 47: key length, 0 when not encrypted
    SealHeader(h);

    var second = new byte[SecondBlockSize + (hasResource ? ResourceRecordSize + (resourceKey?.Length ?? 0) : 0)];
    if (hasResource) second[1] = 0x01;
    StuffItWriter.WriteAscii4(second.AsSpan(4), f.Type);
    StuffItWriter.WriteAscii4(second.AsSpan(8), f.Creator);
    BinaryPrimitives.WriteUInt32BigEndian(second.AsSpan(16), date);
    if (hasResource) {
      var r = second.AsSpan(SecondBlockSize);
      BinaryPrimitives.WriteUInt32BigEndian(r, (uint)f.Resource.Length);
      BinaryPrimitives.WriteUInt32BigEndian(r[4..], (uint)f.PackedResource.Length);
      BinaryPrimitives.WriteUInt16BigEndian(r[8..], StuffItWriter.ComputeCrc16(f.Resource));
      r[12] = f.ResourceMethod;
      if (resourceKey != null) {
        r[13] = (byte)resourceKey.Length;
        resourceKey.CopyTo(r[ResourceRecordSize..]);
      }
    }
    BinaryPrimitives.WriteUInt16BigEndian(second.AsSpan(2), StuffItWriter.ComputeCrc16(second));

    this._stream.Write(h);
    this._stream.Write(second);
    this._stream.Write(this.Encrypt(f.PackedResource, resourceKey));
    this._stream.Write(this.Encrypt(f.PackedData, dataKey));
  }

  private static byte[] NewKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(StuffIt5Crypto.KeyLength);

  private byte[] Encrypt(byte[] packed, byte[]? forkKey) {
    if (forkKey == null || this._archiveKey == null) return packed;
    var copy = (byte[])packed.Clone();
    StuffIt5Crypto.Decrypt(copy, this._archiveKey, forkKey);
    return copy;
  }

  private void WriteFolder(Placed e, long parent, long previous, long next) {
    var flags = (byte)(FlagFolder | (this._archiveKey != null ? StuffIt5Crypto.EntryFlagEncrypted : 0));
    var h = EntryHeader(e.Name, flags, StuffItWriter.ToMacTimestamp(e.Node.Modified), parent, previous, next);
    var contentStart = e.Offset + EntryFixedSize + e.Name.Length + SecondBlockSize;
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(34), (uint)(e.Children.Count > 0 ? e.Children[0].Offset : e.EndRecord));
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(38), (uint)e.FilesSize);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(42), (uint)(e.Offset + e.Size - contentStart));
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(46), (ushort)e.Children.Count);
    SealHeader(h);

    var second = new byte[SecondBlockSize];
    BinaryPrimitives.WriteUInt32BigEndian(second.AsSpan(4), 0x10); // as StuffIt writes it for folders
    BinaryPrimitives.WriteUInt16BigEndian(second.AsSpan(2), StuffItWriter.ComputeCrc16(second));

    this._stream.Write(h);
    this._stream.Write(second);
  }

  private void WriteEndRecord(long previous, long parent) {
    var h = EntryHeader([], FlagFolder, 0, parent, previous, 0);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(34), 0xFFFFFFFF);
    SealHeader(h);
    this._stream.Write(h);
  }
}
