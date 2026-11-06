using System.Buffers.Binary;
using System.Text;

namespace FileFormat.StuffIt;

/// <summary>
/// Creates StuffIt 5 archives (the container of StuffIt 5 to 8): files at the root level, each fork
/// stored, RLE-packed, LZ+Huffman-compressed (method 13) or Arsenic-compressed (method 15), whichever is smallest.
/// </summary>
/// <remarks>
/// <para>
/// Layout, as StuffIt 7 for Windows writes it:
/// <list type="bullet">
///   <item>A 100-byte archive header: 80 bytes of signature text, <c>1A 00</c>, version 5, flags
///   0, the total archive size, the offset of the first entry (twice, around the root entry
///   count), and a CRC-16/ARC over the 100 bytes with its own field zeroed.</item>
///   <item>Per file, an entry header (version 3) of 48 bytes plus the name. It holds the
///   <c>A5A5A5A5</c> marker, the header size, the creation and modification dates, the offsets of
///   the previous and next entries and of the parent folder (0 at the root), the name length, the
///   header CRC, the data fork's size, compressed size, CRC-16 and method.</item>
///   <item>A 32-byte second block: fork flags (bit 0 = resource fork present), its own CRC-16,
///   type, creator and Finder flags.</item>
///   <item>An optional 14-byte resource fork record.</item>
///   <item>Then the resource fork data, followed by the data fork data.</item>
/// </list>
/// </para>
/// <para>
/// Both CRCs are CRC-16/ARC: the header CRC covers the header with its own field zeroed, and the
/// second block's CRC covers that block plus the resource record, again with its own field zeroed.
/// Both rules were measured on archives StuffIt wrote.
/// </para>
/// </remarks>
public sealed class StuffIt5Writer : IDisposable {
  private const string SignatureText = "StuffIt (c)1997-2001 Aladdin Systems, Inc., http://www.aladdinsys.com/StuffIt/\r\n";
  private const int ArchiveHeaderSize = 100;
  private const int EntryFixedSize = 48;
  private const int SecondBlockSize = 32;
  private const int ResourceRecordSize = 14;
  private const uint EntryMarker = 0xA5A5A5A5;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<Pending> _entries = [];
  private readonly StuffItCompression _compression;
  private bool _disposed;

  private sealed record Pending(byte[] Name, string Type, string Creator, uint Date, byte[] Data, byte[] Resource,
    byte[] PackedData, byte DataMethod, byte[] PackedResource, byte ResourceMethod);

  /// <summary>Starts a StuffIt 5 archive on <paramref name="stream"/>; it is written on <see cref="Dispose"/>.</summary>
  public StuffIt5Writer(Stream stream, bool leaveOpen = false) : this(stream, StuffItCompression.Smallest, leaveOpen) { }

  /// <summary>Starts a StuffIt 5 archive whose forks are compressed as <paramref name="compression"/> asks.</summary>
  public StuffIt5Writer(Stream stream, StuffItCompression compression, bool leaveOpen = false) {
    this._compression = compression;
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file with a data fork and an optional resource fork.</summary>
  /// <exception cref="NotSupportedException">The name is empty, longer than 255 bytes, or not representable in Latin-1.</exception>
  public void AddFile(string fileName, byte[] data, byte[]? resourceFork = null, string fileType = "TEXT", string fileCreator = "CWIE", DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    var name = Encoding.Latin1.GetBytes(fileName);
    if (name.Length is 0 or > 255 || Encoding.Latin1.GetString(name) != fileName)
      throw new NotSupportedException($"A StuffIt 5 name holds 1-255 Latin-1 bytes; '{fileName}' does not fit.");
    resourceFork ??= [];
    var packedData = StuffItWriter.CompressFork(data, out var dataMethod, this._compression);
    var packedResource = StuffItWriter.CompressFork(resourceFork, out var resourceMethod, this._compression);
    this._entries.Add(new Pending(name, fileType, fileCreator, StuffItWriter.ToMacTimestamp(lastModified ?? DateTime.UtcNow),
      data, resourceFork, packedData, dataMethod, packedResource, resourceMethod));
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this.Write();
    if (!this._leaveOpen) this._stream.Dispose();
  }

  private static int EntrySize(Pending e)
    => EntryFixedSize + e.Name.Length + SecondBlockSize + (e.Resource.Length > 0 ? ResourceRecordSize : 0) + e.PackedResource.Length + e.PackedData.Length;

  private void Write() {
    var offsets = new long[this._entries.Count];
    long position = ArchiveHeaderSize;
    for (var i = 0; i < this._entries.Count; ++i) {
      offsets[i] = position;
      position += EntrySize(this._entries[i]);
    }
    if (position > uint.MaxValue) throw new NotSupportedException("A StuffIt 5 archive is limited to 4 GiB.");

    var header = new byte[ArchiveHeaderSize];
    Encoding.ASCII.GetBytes(SignatureText).CopyTo(header, 0);
    header[80] = 0x1A;
    header[82] = 5;
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(84), (uint)position);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(88), ArchiveHeaderSize);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(92), (ushort)this._entries.Count);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(94), ArchiveHeaderSize);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(98), StuffItWriter.ComputeCrc16(header));
    this._stream.Write(header);

    for (var i = 0; i < this._entries.Count; ++i) {
      var e = this._entries[i];
      var previous = i > 0 ? (uint)offsets[i - 1] : 0u;
      var next = i + 1 < this._entries.Count ? (uint)offsets[i + 1] : 0u;
      this.WriteEntry(e, previous, next);
    }
    this._stream.Flush();
  }

  private void WriteEntry(Pending e, uint previous, uint next) {
    var hasResource = e.Resource.Length > 0;

    var h = new byte[EntryFixedSize + e.Name.Length];
    BinaryPrimitives.WriteUInt32BigEndian(h, EntryMarker);
    h[4] = 3; // entry version
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(6), (ushort)h.Length);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(10), e.Date);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(14), e.Date);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(18), previous);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(22), next);
    // 26: parent folder offset, 0 at the root
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(30), (ushort)e.Name.Length);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(34), (uint)e.Data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(38), (uint)e.PackedData.Length);
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(42), StuffItWriter.ComputeCrc16(e.Data));
    h[46] = e.DataMethod;
    // 47: key length, 0 when not encrypted
    e.Name.CopyTo(h, EntryFixedSize);
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(32), StuffItWriter.ComputeCrc16(h));

    var second = new byte[SecondBlockSize + (hasResource ? ResourceRecordSize : 0)];
    if (hasResource) second[1] = 0x01;
    StuffItWriter.WriteAscii4(second.AsSpan(4), e.Type);
    StuffItWriter.WriteAscii4(second.AsSpan(8), e.Creator);
    BinaryPrimitives.WriteUInt32BigEndian(second.AsSpan(16), e.Date);
    if (hasResource) {
      var r = second.AsSpan(SecondBlockSize);
      BinaryPrimitives.WriteUInt32BigEndian(r, (uint)e.Resource.Length);
      BinaryPrimitives.WriteUInt32BigEndian(r[4..], (uint)e.PackedResource.Length);
      BinaryPrimitives.WriteUInt16BigEndian(r[8..], StuffItWriter.ComputeCrc16(e.Resource));
      r[12] = e.ResourceMethod;
    }
    BinaryPrimitives.WriteUInt16BigEndian(second.AsSpan(2), StuffItWriter.ComputeCrc16(second));

    this._stream.Write(h);
    this._stream.Write(second);
    this._stream.Write(e.PackedResource);
    this._stream.Write(e.PackedData);
  }
}
