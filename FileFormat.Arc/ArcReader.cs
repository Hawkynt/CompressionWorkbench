using System.Text;
using Compression.Core.BitIO;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Arc;

/// <summary>
/// Reads entries sequentially from an ARC archive.
/// </summary>
public sealed class ArcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;
  private bool _endOfArchive;
  private ArcEntry? _currentEntry;
  private long _entryDataStart;

  /// <summary>
  /// Initializes a new <see cref="ArcReader"/> from a stream containing ARC archive data.
  /// </summary>
  /// <param name="stream">The stream to read the ARC archive from.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  public ArcReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Reads the next entry header from the archive.
  /// </summary>
  /// <returns>
  /// The next <see cref="ArcEntry"/>, or <see langword="null"/> when the end of the archive is reached.
  /// </returns>
  /// <exception cref="InvalidDataException">Thrown when the stream contains malformed ARC data.</exception>
  public ArcEntry? GetNextEntry() {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._endOfArchive)
      return null;

    // Skip over any unread data from the current entry.
    SkipCurrentEntryData();

    // Read and validate the magic byte.
    int magic = this._stream.ReadByte();
    if (magic == -1)
      return null; // EOF — treat as end of archive.

    if (magic != ArcConstants.Magic)
      throw new InvalidDataException($"Expected ARC magic byte 0x{ArcConstants.Magic:X2}, found 0x{magic:X2}.");

    // Read the method byte.
    int method = this._stream.ReadByte();
    if (method == -1)
      throw new InvalidDataException("Unexpected end of stream reading ARC entry method.");

    if (method == ArcConstants.MethodEndOfArchive) {
      this._endOfArchive = true;
      return null;
    }

    // Read the rest of the fixed-size header fields.
    // For method 1 (old stored): header is 25 bytes total (2 already read).
    // For method >= 2 (new format): header is 29 bytes total.
    bool isNewFormat = method >= ArcConstants.MethodStored;
    int remainingHeaderBytes = isNewFormat
      ? ArcConstants.NewHeaderSize - 2
      : ArcConstants.OldHeaderSize - 2;

    byte[] headerBuf = new byte[remainingHeaderBytes];
    ReadExact(headerBuf, 0, remainingHeaderBytes);

    // Filename: 13 bytes, null-terminated.
    string fileName = ReadNullTerminatedAscii(headerBuf, 0, ArcConstants.FileNameLength);

    // Compressed size: uint32 LE at offset 13.
    uint compressedSize = ReadUInt32Le(headerBuf, 13);

    // Date: uint16 LE at offset 17.
    ushort dosDate = ReadUInt16Le(headerBuf, 17);

    // Time: uint16 LE at offset 19.
    ushort dosTime = ReadUInt16Le(headerBuf, 19);

    // CRC-16: uint16 LE at offset 21.
    ushort crc16 = ReadUInt16Le(headerBuf, 21);

    // Original size: uint32 LE at offset 23 (new format only).
    uint originalSize;
    if (isNewFormat)
      originalSize = ReadUInt32Le(headerBuf, 23);
    else
      originalSize = compressedSize; // old stored format: same as compressed size.

    var entry = new ArcEntry {
      FileName = fileName,
      Method = (byte)method,
      CompressedSize = compressedSize,
      OriginalSize = originalSize,
      DosDate = dosDate,
      DosTime = dosTime,
      Crc16 = crc16,
    };

    this._currentEntry = entry;
    this._entryDataStart = this._stream.Position;
    return entry;
  }

  /// <summary>
  /// Decompresses and returns the data for the current entry.
  /// </summary>
  /// <returns>The decompressed entry data as a byte array.</returns>
  /// <exception cref="InvalidOperationException">Thrown when no entry is currently loaded.</exception>
  /// <exception cref="InvalidDataException">Thrown when the data is corrupt or the CRC does not match.</exception>
  /// <exception cref="NotSupportedException">Thrown when the compression method is not supported.</exception>
  public byte[] ReadEntryData() {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._currentEntry == null)
      throw new InvalidOperationException("No current entry. Call GetNextEntry() first.");

    // Seek back to start of entry data in case the caller reads data twice.
    if (this._stream.CanSeek)
      this._stream.Position = this._entryDataStart;

    byte[] compressedData = ReadExactBytes((int)this._currentEntry.CompressedSize);
    byte[] decompressed = Decompress(this._currentEntry, compressedData);

    // Verify CRC-16.
    ushort actualCrc = Crc16.Compute(decompressed);
    if (actualCrc != this._currentEntry.Crc16)
      throw new InvalidDataException(
        $"CRC-16 mismatch for '{this._currentEntry.FileName}': expected 0x{this._currentEntry.Crc16:X4}, computed 0x{actualCrc:X4}.");

    // Mark data as consumed so GetNextEntry can skip correctly.
    this._currentEntry = null;
    return decompressed;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private static byte[] Decompress(ArcEntry entry, byte[] compressedData) =>
    entry.Method switch {
      ArcConstants.MethodStoredOld or ArcConstants.MethodStored => compressedData,
      ArcConstants.MethodPacked => ArcRle.Decode(compressedData),
      ArcConstants.MethodCrunched8 or ArcConstants.MethodSquashed => DecompressLzw(compressedData, entry),
      ArcConstants.MethodSqueezed =>
        ArcSqueeze.Decode(compressedData, (int)entry.OriginalSize),
      ArcConstants.MethodCrunched5 =>
        ArcCrunch.DecodeCrunched5(compressedData, (int)entry.OriginalSize),
      ArcConstants.MethodCrunched6 =>
        ArcCrunch.DecodeCrunched6(compressedData, (int)entry.OriginalSize),
      ArcConstants.MethodCrunched7 =>
        ArcCrunch.DecodeCrunched7(compressedData, (int)entry.OriginalSize),
      _ => throw new NotSupportedException($"Unknown ARC compression method {entry.Method}."),
    };

  private static byte[] DecompressLzw(byte[] compressedData, ArcEntry entry) {
    using var ms = new MemoryStream(compressedData);
    // Method 8: clear code enabled (dynamic reset). Method 9: no clear code (squashed).
    bool useClearCode = entry.Method == ArcConstants.MethodCrunched8;
    var decoder = new LzwDecoder(
      ms,
      minBits: ArcConstants.LzwMinBits,
      maxBits: ArcConstants.LzwMaxBits,
      useClearCode: useClearCode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    return decoder.Decode((int)entry.OriginalSize);
  }

  private void SkipCurrentEntryData() {
    if (this._currentEntry == null)
      return;

    long bytesToSkip = (this._entryDataStart + this._currentEntry.CompressedSize) - this._stream.Position;
    if (bytesToSkip > 0) {
      if (this._stream.CanSeek)
        this._stream.Position += bytesToSkip;
      else {
        byte[] buf = new byte[Math.Min(bytesToSkip, 4096)];
        while (bytesToSkip > 0) {
          int toRead = (int)Math.Min(bytesToSkip, buf.Length);
          int read = this._stream.Read(buf, 0, toRead);
          if (read == 0) break;
          bytesToSkip -= read;
        }
      }
    }

    this._currentEntry = null;
  }

  private void ReadExact(byte[] buffer, int offset, int count) {
    int totalRead = 0;
    while (totalRead < count) {
      int read = this._stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream reading ARC header.");
      totalRead += read;
    }
  }

  private byte[] ReadExactBytes(int count) {
    byte[] buf = new byte[count];
    ReadExact(buf, 0, count);
    return buf;
  }

  private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int maxLength) {
    int end = offset;
    while (end < offset + maxLength && buffer[end] != 0)
      ++end;
    return Encoding.ASCII.GetString(buffer, offset, end - offset);
  }

  private static ushort ReadUInt16Le(byte[] buffer, int offset) =>
    (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

  private static uint ReadUInt32Le(byte[] buffer, int offset) =>
    (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}
