using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.AlZip;

/// <summary>
/// Creates ALZip (.alz) archive files.
/// </summary>
public sealed class AlZipWriter : IDisposable {

  private const uint LocalSig = 0x015A4C42;
  private const uint EndSig = 0x025A4C43;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;

  /// <summary>
  /// Creates a new ALZip writer over the given stream.
  /// </summary>
  public AlZipWriter(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;

    // Write archive magic
    _stream.Write(AlZipReader.Magic);
  }

  /// <summary>
  /// Adds a file to the archive with deflate compression.
  /// </summary>
  public void AddFile(string fileName, byte[] data) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
    var crc = Crc32.Compute(data);
    var compressed = data.Length > 0
      ? DeflateCompressor.Compress(data)
      : [];

    // Use store if deflate doesn't help
    var method = (byte)2; // Deflate
    var compData = compressed;
    if (compressed.Length >= data.Length) {
      method = 0; // Store
      compData = data;
    }

    WriteUInt32LE(LocalSig);
    WriteUInt16LE((ushort)nameBytes.Length);
    _stream.WriteByte(0x20); // Archive attribute
    WriteUInt32LE(AlZipReader.DateTimeToDosTime(DateTime.Now));

    // File descriptor: 0x20 = 4-byte sizes
    _stream.WriteByte(0x20);

    // Reserved
    _stream.WriteByte(0);

    // Method
    _stream.WriteByte(method);

    // CRC-32
    WriteUInt32LE(crc);

    // Compressed size (4 bytes)
    WriteUInt32LE((uint)compData.Length);

    // Uncompressed size (4 bytes)
    WriteUInt32LE((uint)data.Length);

    // Filename
    _stream.Write(nameBytes);

    // Data
    _stream.Write(compData);
  }

  /// <summary>
  /// Adds a directory entry to the archive.
  /// </summary>
  public void AddDirectory(string dirName) {
    ArgumentNullException.ThrowIfNull(dirName);

    var nameBytes = System.Text.Encoding.UTF8.GetBytes(dirName);

    WriteUInt32LE(LocalSig);
    WriteUInt16LE((ushort)nameBytes.Length);
    _stream.WriteByte(0x10); // Directory attribute
    WriteUInt32LE(AlZipReader.DateTimeToDosTime(DateTime.Now));
    _stream.WriteByte(0x20); // 4-byte sizes
    _stream.WriteByte(0);    // Reserved
    _stream.WriteByte(0);    // Store
    WriteUInt32LE(0);        // CRC
    WriteUInt32LE(0);        // Compressed size
    WriteUInt32LE(0);        // Uncompressed size
    _stream.Write(nameBytes);
  }

  /// <inheritdoc />
  public void Dispose() {
    // Write end-of-archive marker
    WriteUInt32LE(EndSig);

    if (!_leaveOpen)
      _stream.Dispose();
  }

  private void WriteUInt16LE(ushort value) {
    Span<byte> buf = stackalloc byte[2];
    buf[0] = (byte)value;
    buf[1] = (byte)(value >> 8);
    _stream.Write(buf);
  }

  private void WriteUInt32LE(uint value) {
    Span<byte> buf = stackalloc byte[4];
    buf[0] = (byte)value;
    buf[1] = (byte)(value >> 8);
    buf[2] = (byte)(value >> 16);
    buf[3] = (byte)(value >> 24);
    _stream.Write(buf);
  }
}
