using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Spark;

/// <summary>
/// Creates a RISC OS Spark archive (.spk) by writing entries sequentially to a stream.
/// </summary>
/// <remarks>
/// Files are stored uncompressed (method 0x02 for plain ARC entries, or 0x82 for directories
/// with RISC OS extensions). RISC OS load/exec addresses and file attributes are written
/// when non-zero values are provided.
/// </remarks>
public sealed class SparkWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _finished;
  private bool _disposed;
  private int _directoryDepth;

  /// <summary>
  /// Initializes a new <see cref="SparkWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the Spark archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  public SparkWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file entry to the archive using the Stored method.
  /// </summary>
  /// <param name="name">The filename to store (up to 12 characters for the standard field).</param>
  /// <param name="data">The uncompressed file data.</param>
  /// <param name="lastModified">The last-modified timestamp. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  /// <param name="loadAddress">The RISC OS load address (0 if not applicable).</param>
  /// <param name="execAddress">The RISC OS execution address (0 if not applicable).</param>
  /// <param name="fileAttributes">The RISC OS file attributes (0 if not applicable).</param>
  public void AddFile(string name, byte[] data, DateTime? lastModified = null,
      uint loadAddress = 0, uint execAddress = 0, uint fileAttributes = 0) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after the archive has been finished.");
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length == 0)
      throw new ArgumentException("File name must not be empty.", nameof(name));

    DateTime timestamp = lastModified ?? DateTime.UtcNow;
    bool hasRiscOs = loadAddress != 0 || execAddress != 0 || fileAttributes != 0;
    byte method = hasRiscOs ? SparkConstants.MethodStored : SparkConstants.MethodStored;
    // When RISC OS metadata is present, use Spark extended stored (0x82).
    if (hasRiscOs)
      method = (byte)(SparkConstants.MethodStored | 0x80);

    ushort crc = Crc16.Compute(data);

    WriteEntryHeader(name, method, (uint)data.Length, (uint)data.Length, crc, timestamp,
      loadAddress, execAddress, fileAttributes);
    this._stream.Write(data, 0, data.Length);
  }

  /// <summary>
  /// Begins a directory entry in the archive.
  /// Subsequent calls to <see cref="AddFile"/> and <see cref="BeginDirectory"/> will add
  /// entries inside this directory until <see cref="EndDirectory"/> is called.
  /// </summary>
  /// <param name="name">The directory name.</param>
  /// <param name="lastModified">The last-modified timestamp. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  /// <param name="loadAddress">The RISC OS load address.</param>
  /// <param name="execAddress">The RISC OS execution address.</param>
  /// <param name="fileAttributes">The RISC OS file attributes.</param>
  public void BeginDirectory(string name, DateTime? lastModified = null,
      uint loadAddress = 0, uint execAddress = 0, uint fileAttributes = 0) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after the archive has been finished.");
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length == 0)
      throw new ArgumentException("Directory name must not be empty.", nameof(name));

    DateTime timestamp = lastModified ?? DateTime.UtcNow;

    // Directory entry: method 0x82, zero compressed/original size, zero CRC.
    WriteEntryHeader(name, SparkConstants.MethodDirectory, compressedSize: 0, originalSize: 0,
      crc16: 0, timestamp, loadAddress, execAddress, fileAttributes);

    this._directoryDepth++;
  }

  /// <summary>
  /// Ends the current directory by writing an end-of-directory marker (method 0x80).
  /// </summary>
  /// <exception cref="InvalidOperationException">
  /// Thrown when there is no open directory to end.
  /// </exception>
  public void EndDirectory() {
    if (this._finished)
      throw new InvalidOperationException("Cannot modify the archive after it has been finished.");
    if (this._directoryDepth <= 0)
      throw new InvalidOperationException("No open directory to end.");

    // End-of-directory: marker + method 0x80 + 13 zero bytes (filename) + 4 zero (compressed size)
    // + 2 zero (date) + 2 zero (time) + 2 zero (CRC) + 4 zero (original size) = 29 bytes total.
    // But the minimal marker is just marker + 0x80.
    // In practice, a full header is written with zeroed fields.
    this._stream.WriteByte(SparkConstants.EntryMarker);
    this._stream.WriteByte(SparkConstants.MethodEndOfDirectory);

    this._directoryDepth--;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private void Finish() {
    if (this._finished)
      return;

    // Close any open directories.
    while (this._directoryDepth > 0)
      EndDirectory();

    this._finished = true;

    // End-of-archive marker.
    this._stream.WriteByte(SparkConstants.EntryMarker);
    this._stream.WriteByte(SparkConstants.MethodEndOfArchive);
    this._stream.Flush();
  }

  private void WriteEntryHeader(string name, byte method, uint compressedSize, uint originalSize,
      ushort crc16, DateTime timestamp, uint loadAddress, uint execAddress, uint fileAttributes) {
    bool isSparkExtended = SparkConstants.IsSparkExtended(method);
    byte baseMethod = SparkConstants.GetBaseMethod(method);
    bool hasOriginalSize = baseMethod >= SparkConstants.GetBaseMethod(SparkConstants.MethodStored);

    // Calculate header size: 2 (marker+method) + 13 (name) + 4 (compSize) + 2 (date) + 2 (time) + 2 (crc)
    // = 25 for old format; + 4 (origSize) = 29 for new format; + 12 (RISC OS) = 41 if Spark extended.
    int headerSize = 2 + SparkConstants.FileNameLength + 4 + 2 + 2 + 2;
    if (hasOriginalSize)
      headerSize += 4;
    if (isSparkExtended)
      headerSize += SparkConstants.RiscOsExtensionSize;

    byte[] header = new byte[headerSize];
    int offset = 0;

    // Marker + method.
    header[offset++] = SparkConstants.EntryMarker;
    header[offset++] = method;

    // Filename: 13 bytes, null-terminated.
    string truncatedName = name.Length > 12 ? name[..12] : name;
    byte[] nameBytes = Encoding.ASCII.GetBytes(truncatedName);
    int nameLen = Math.Min(nameBytes.Length, SparkConstants.FileNameLength - 1);
    Array.Copy(nameBytes, 0, header, offset, nameLen);
    // Remaining bytes in name field stay zero (null-terminated + zero-padded).
    offset += SparkConstants.FileNameLength;

    // Compressed size: 4 bytes LE.
    WriteUInt32Le(header, offset, compressedSize);
    offset += 4;

    // MS-DOS date and time.
    var (dosDate, dosTime) = SparkEntry.DateTimeToDosDateTime(timestamp);
    WriteUInt16Le(header, offset, dosDate);
    offset += 2;
    WriteUInt16Le(header, offset, dosTime);
    offset += 2;

    // CRC-16.
    WriteUInt16Le(header, offset, crc16);
    offset += 2;

    // Original size (new format only).
    if (hasOriginalSize) {
      WriteUInt32Le(header, offset, originalSize);
      offset += 4;
    }

    // RISC OS extension.
    if (isSparkExtended) {
      WriteUInt32Le(header, offset, loadAddress);
      offset += 4;
      WriteUInt32Le(header, offset, execAddress);
      offset += 4;
      WriteUInt32Le(header, offset, fileAttributes);
      // offset += 4; // not needed, end of header
    }

    this._stream.Write(header, 0, header.Length);
  }

  private static void WriteUInt16Le(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
  }

  private static void WriteUInt32Le(byte[] buffer, int offset, uint value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
    buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
  }
}
