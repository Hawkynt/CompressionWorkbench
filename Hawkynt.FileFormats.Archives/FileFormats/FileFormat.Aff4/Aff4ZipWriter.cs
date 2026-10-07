using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Streams;

namespace FileFormat.Aff4;

/// <summary>
/// The ZIP layer of an AFF4 volume. AFF4 Standard v1.0 section 5.4 requires every ZIP header to be
/// ZIP64, which general-purpose ZIP writers do not do for small members. Each member is written the
/// way the AFF4 Canonical Reference Images (Evimetry) write them, streaming and without seeking:
/// a version-4.5 local header whose 32-bit sizes are 0xFFFFFFFF and which carries a ZIP64 extra
/// field, then the data, then a ZIP64 data descriptor (PKWARE APPNOTE 6.3.10 sections 4.3.9 and
/// 4.5.3: with bit 3 set the local ZIP64 sizes are zero). The central directory keeps 32-bit
/// fields where values fit and moves the rest into a ZIP64 extra field, and a ZIP64 end record is
/// added once the entry count, directory size or offset outgrows the classic end record.
/// </summary>
internal sealed class Aff4ZipWriter {
  private const ushort Version45 = 45;
  private const ushort FlagDataDescriptor = 0x0008;
  private const ushort FlagUtf8 = 0x0800;

  private sealed record CentralEntry(byte[] Name, ushort Flags, ushort Method, ushort DosTime, ushort DosDate, uint Crc, long CompressedSize, long Size, long Offset);

  private readonly Stream _output;
  private readonly List<CentralEntry> _entries = [];
  private long _position;

  public Aff4ZipWriter(Stream output) => this._output = output;

  /// <summary>Writes one member, Stored or Deflate, from <paramref name="source"/>; every read byte is also fed to <paramref name="observe"/>.</summary>
  public void AddEntry(string name, Stream source, bool deflate, DeflateCompressionLevel level, DateTimeOffset modified, Action<ReadOnlySpan<byte>>? observe = null) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    if (nameBytes.Length > ushort.MaxValue) throw new ArgumentException($"ZIP member name '{name}' is too long.", nameof(name));
    var flags = (ushort)(FlagDataDescriptor | (name.Any(c => c > 0x7F) ? FlagUtf8 : 0));
    var method = (ushort)(deflate ? 8 : 0);
    var (dosTime, dosDate) = ToDos(modified);
    var offset = this._position;

    Span<byte> header = stackalloc byte[30];
    BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034B50);
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version45);
    BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
    BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
    BinaryPrimitives.WriteUInt16LittleEndian(header[10..], dosTime);
    BinaryPrimitives.WriteUInt16LittleEndian(header[12..], dosDate);
    BinaryPrimitives.WriteUInt32LittleEndian(header[14..], 0); // CRC follows in the data descriptor
    BinaryPrimitives.WriteUInt32LittleEndian(header[18..], uint.MaxValue);
    BinaryPrimitives.WriteUInt32LittleEndian(header[22..], uint.MaxValue);
    BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)nameBytes.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 20);
    this.Write(header);
    this.Write(nameBytes);
    Span<byte> extra = stackalloc byte[20];
    extra.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
    BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
    this.Write(extra);

    var crc = new Crc32();
    long size = 0;
    var dataStart = this._position;
    var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
    try {
      var counter = new CountingStream(this);
      using (var sink = deflate ? new RawDeflateStream(counter, CompressionStreamMode.Compress, level, leaveOpen: true) : (Stream)counter) {
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
          var chunk = buffer.AsSpan(0, read);
          crc.Update(chunk);
          observe?.Invoke(chunk);
          sink.Write(chunk);
          size += read;
        }
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
    var compressedSize = this._position - dataStart;

    Span<byte> descriptor = stackalloc byte[24];
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x08074B50);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], crc.Value);
    BinaryPrimitives.WriteInt64LittleEndian(descriptor[8..], compressedSize);
    BinaryPrimitives.WriteInt64LittleEndian(descriptor[16..], size);
    this.Write(descriptor);
    this._entries.Add(new(nameBytes, flags, method, dosTime, dosDate, crc.Value, compressedSize, size, offset));
  }

  /// <summary>Writes the central directory and end records; <paramref name="comment"/> becomes the ZIP comment.</summary>
  public void Finish(string comment) {
    var commentBytes = Encoding.UTF8.GetBytes(comment);
    var directoryOffset = this._position;
    foreach (var e in this._entries) {
      var needSize = e.Size >= uint.MaxValue;
      var needCompressed = e.CompressedSize >= uint.MaxValue;
      var needOffset = e.Offset >= uint.MaxValue;
      var extraLength = (needSize ? 8 : 0) + (needCompressed ? 8 : 0) + (needOffset ? 8 : 0);
      var record = new byte[46 + e.Name.Length + (extraLength > 0 ? 4 + extraLength : 0)];
      var span = record.AsSpan();
      BinaryPrimitives.WriteUInt32LittleEndian(span, 0x02014B50);
      BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version45);
      BinaryPrimitives.WriteUInt16LittleEndian(span[6..], Version45);
      BinaryPrimitives.WriteUInt16LittleEndian(span[8..], e.Flags);
      BinaryPrimitives.WriteUInt16LittleEndian(span[10..], e.Method);
      BinaryPrimitives.WriteUInt16LittleEndian(span[12..], e.DosTime);
      BinaryPrimitives.WriteUInt16LittleEndian(span[14..], e.DosDate);
      BinaryPrimitives.WriteUInt32LittleEndian(span[16..], e.Crc);
      BinaryPrimitives.WriteUInt32LittleEndian(span[20..], needCompressed ? uint.MaxValue : (uint)e.CompressedSize);
      BinaryPrimitives.WriteUInt32LittleEndian(span[24..], needSize ? uint.MaxValue : (uint)e.Size);
      BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)e.Name.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)(extraLength > 0 ? 4 + extraLength : 0));
      BinaryPrimitives.WriteUInt32LittleEndian(span[42..], needOffset ? uint.MaxValue : (uint)e.Offset);
      e.Name.CopyTo(span[46..]);
      if (extraLength > 0) {
        var x = span[(46 + e.Name.Length)..];
        BinaryPrimitives.WriteUInt16LittleEndian(x, 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(x[2..], (ushort)extraLength);
        var p = 4;
        // APPNOTE 4.5.3: only the fields set to 0xFFFFFFFF appear, in this order.
        if (needSize) { BinaryPrimitives.WriteInt64LittleEndian(x[p..], e.Size); p += 8; }
        if (needCompressed) { BinaryPrimitives.WriteInt64LittleEndian(x[p..], e.CompressedSize); p += 8; }
        if (needOffset) BinaryPrimitives.WriteInt64LittleEndian(x[p..], e.Offset);
      }
      this.Write(record);
    }
    var directorySize = this._position - directoryOffset;
    var count = this._entries.Count;
    var needZip64End = count >= ushort.MaxValue || directorySize >= uint.MaxValue || directoryOffset >= uint.MaxValue;
    if (needZip64End) {
      var zip64EndOffset = this._position;
      Span<byte> end64 = stackalloc byte[56];
      BinaryPrimitives.WriteUInt32LittleEndian(end64, 0x06064B50);
      BinaryPrimitives.WriteInt64LittleEndian(end64[4..], 44);
      BinaryPrimitives.WriteUInt16LittleEndian(end64[12..], Version45);
      BinaryPrimitives.WriteUInt16LittleEndian(end64[14..], Version45);
      BinaryPrimitives.WriteUInt32LittleEndian(end64[16..], 0);
      BinaryPrimitives.WriteUInt32LittleEndian(end64[20..], 0);
      BinaryPrimitives.WriteInt64LittleEndian(end64[24..], count);
      BinaryPrimitives.WriteInt64LittleEndian(end64[32..], count);
      BinaryPrimitives.WriteInt64LittleEndian(end64[40..], directorySize);
      BinaryPrimitives.WriteInt64LittleEndian(end64[48..], directoryOffset);
      this.Write(end64);
      Span<byte> locator = stackalloc byte[20];
      BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064B50);
      BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);
      BinaryPrimitives.WriteInt64LittleEndian(locator[8..], zip64EndOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
      this.Write(locator);
    }
    Span<byte> end = stackalloc byte[22];
    BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
    var count16 = (ushort)Math.Min(count, ushort.MaxValue);
    BinaryPrimitives.WriteUInt16LittleEndian(end[8..], count16);
    BinaryPrimitives.WriteUInt16LittleEndian(end[10..], count16);
    BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)Math.Min(directorySize, uint.MaxValue));
    BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)Math.Min(directoryOffset, uint.MaxValue));
    BinaryPrimitives.WriteUInt16LittleEndian(end[20..], (ushort)commentBytes.Length);
    this.Write(end);
    this.Write(commentBytes);
    this._output.Flush();
  }

  private void Write(ReadOnlySpan<byte> data) {
    this._output.Write(data);
    this._position += data.Length;
  }

  private static (ushort Time, ushort Date) ToDos(DateTimeOffset value) {
    var t = value.UtcDateTime;
    if (t.Year < 1980) t = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
    return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
  }

  /// <summary>Forwards writes to the owning writer so compressed bytes are counted.</summary>
  private sealed class CountingStream(Aff4ZipWriter owner) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => owner.Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => owner.Write(buffer);
  }
}
