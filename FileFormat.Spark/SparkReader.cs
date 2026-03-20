using System.Text;
using Compression.Core.BitIO;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Spark;

/// <summary>
/// Reads entries from a RISC OS Spark archive (.spk).
/// </summary>
/// <remarks>
/// Spark archives use the ARC container format extended with RISC OS metadata
/// (load address, exec address, file attributes) and directory support.
/// </remarks>
public sealed class SparkReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<SparkEntry> _entries = [];
  private bool _disposed;
  private bool _parsed;

  /// <summary>
  /// Initializes a new <see cref="SparkReader"/> from a stream containing Spark archive data.
  /// </summary>
  /// <param name="stream">The stream to read the Spark archive from.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  public SparkReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Gets all entries in the archive. The archive is parsed on first access.
  /// </summary>
  public IReadOnlyList<SparkEntry> Entries {
    get {
      if (!this._parsed)
        ParseArchive();
      return this._entries;
    }
  }

  /// <summary>
  /// Extracts and decompresses the data for the specified entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed data as a byte array.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidDataException">Thrown when the data is corrupt or the CRC does not match.</exception>
  /// <exception cref="NotSupportedException">Thrown when the compression method is not supported.</exception>
  public byte[] Extract(SparkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.IsDirectory)
      return [];

    this._stream.Position = entry.DataOffset;
    byte[] compressed = ReadExactBytes((int)entry.CompressedSize);
    byte[] decompressed = Decompress(entry.Method, compressed, (int)entry.OriginalSize);

    // Verify CRC-16.
    ushort actualCrc = Crc16.Compute(decompressed);
    if (actualCrc != entry.Crc16)
      throw new InvalidDataException(
        $"CRC-16 mismatch for '{entry.FileName}': expected 0x{entry.Crc16:X4}, computed 0x{actualCrc:X4}.");

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

  private void ParseArchive() {
    this._parsed = true;
    this._entries.Clear();
    ParseEntries(this._entries, prefix: string.Empty);
  }

  private void ParseEntries(List<SparkEntry> entries, string prefix) {
    while (true) {
      int marker = this._stream.ReadByte();
      if (marker == -1)
        return; // EOF — treat as end of archive.

      if (marker != SparkConstants.EntryMarker)
        throw new InvalidDataException(
          $"Expected Spark entry marker 0x{SparkConstants.EntryMarker:X2}, found 0x{marker:X2}.");

      int method = this._stream.ReadByte();
      if (method == -1)
        throw new InvalidDataException("Unexpected end of stream reading Spark entry method.");

      // End of archive.
      if (method == SparkConstants.MethodEndOfArchive)
        return;

      // End of directory — return to parent.
      if (method == SparkConstants.MethodEndOfDirectory)
        return;

      byte methodByte = (byte)method;
      bool isSparkExtended = SparkConstants.IsSparkExtended(methodByte);
      byte baseMethod = SparkConstants.GetBaseMethod(methodByte);

      // Determine header format: method 1 (old stored) has no original-size field.
      bool hasOriginalSize = baseMethod >= SparkConstants.GetBaseMethod(SparkConstants.MethodStored);

      // Read the fixed header fields after marker+method (already consumed 2 bytes).
      // Filename: 13 bytes.
      byte[] nameBuf = ReadExactBytes(SparkConstants.FileNameLength);
      string fileName = ReadNullTerminatedAscii(nameBuf);

      // Compressed size: 4 bytes LE.
      uint compressedSize = ReadUInt32Le();

      // Date and time: 2 + 2 bytes LE (MS-DOS format).
      ushort dosDate = ReadUInt16Le();
      ushort dosTime = ReadUInt16Le();

      // CRC-16: 2 bytes LE.
      ushort crc16 = ReadUInt16Le();

      // Original size: 4 bytes LE (only for methods >= 2).
      uint originalSize;
      if (hasOriginalSize)
        originalSize = ReadUInt32Le();
      else
        originalSize = compressedSize;

      // RISC OS extension: 12 bytes (load + exec + attributes).
      uint loadAddress = 0;
      uint execAddress = 0;
      uint fileAttributes = 0;

      if (isSparkExtended || methodByte == SparkConstants.MethodCompressed) {
        loadAddress = ReadUInt32Le();
        execAddress = ReadUInt32Le();
        fileAttributes = ReadUInt32Le();
      }

      DateTime lastModified = SparkEntry.DosDateTimeToDateTime(dosDate, dosTime);
      bool isDirectory = methodByte == SparkConstants.MethodDirectory;

      string fullName = prefix.Length > 0 ? $"{prefix}/{fileName}" : fileName;

      long dataOffset = this._stream.Position;

      var entry = new SparkEntry {
        FileName = fullName,
        Method = methodByte,
        OriginalSize = originalSize,
        CompressedSize = compressedSize,
        Crc16 = crc16,
        LastModified = lastModified,
        IsDirectory = isDirectory,
        LoadAddress = loadAddress,
        ExecAddress = execAddress,
        FileAttributes = fileAttributes,
        DataOffset = dataOffset,
      };

      entries.Add(entry);

      if (isDirectory) {
        // For directories, the compressed data contains nested entries.
        // We need to read into the directory data to parse sub-entries,
        // then the directory ends with an end-of-directory marker (0x80).
        ParseEntries(entries, fullName);
      } else {
        // Skip over the compressed data for non-directory entries.
        if (compressedSize > 0) {
          if (this._stream.CanSeek)
            this._stream.Position += compressedSize;
          else
            SkipBytes(compressedSize);
        }
      }
    }
  }

  private static byte[] Decompress(byte method, byte[] compressedData, int originalSize) {
    byte baseMethod = SparkConstants.GetBaseMethod(method);

    return baseMethod switch {
      0x01 or 0x02 => compressedData, // Stored
      0x03 => DecodeRle(compressedData), // Packed (RLE)
      0x08 => DecompressLzw(compressedData, originalSize, useClearCode: true), // Crunched variable
      0x09 => DecompressLzw(compressedData, originalSize, useClearCode: false), // Squashed
      _ when method == SparkConstants.MethodCompressed =>
        DecompressLzw(compressedData, originalSize, useClearCode: false),
      _ => throw new NotSupportedException(
        $"Spark compression method 0x{method:X2} is not supported."),
    };
  }

  private static byte[] DecodeRle(byte[] data) {
    if (data.Length == 0)
      return [];

    var output = new List<byte>(data.Length * 2);
    byte lastByte = 0;
    int i = 0;

    while (i < data.Length) {
      byte current = data[i++];

      if (current != SparkConstants.RleMarker) {
        output.Add(current);
        lastByte = current;
        continue;
      }

      if (i >= data.Length)
        throw new InvalidDataException("Unexpected end of Spark RLE stream after marker byte.");

      byte count = data[i++];

      if (count == 0) {
        // Literal 0x90.
        output.Add(SparkConstants.RleMarker);
        lastByte = SparkConstants.RleMarker;
        continue;
      }

      // Repeat lastByte (count - 1) additional times.
      for (int r = 1; r < count; r++)
        output.Add(lastByte);
    }

    return [.. output];
  }

  private static byte[] DecompressLzw(byte[] compressedData, int originalSize, bool useClearCode) {
    using var ms = new MemoryStream(compressedData);
    var decoder = new LzwDecoder(
      ms,
      minBits: SparkConstants.LzwMinBits,
      maxBits: SparkConstants.LzwMaxBits,
      useClearCode: useClearCode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    return decoder.Decode(originalSize);
  }

  private void SkipBytes(uint count) {
    byte[] buf = new byte[Math.Min(count, 4096)];
    uint remaining = count;
    while (remaining > 0) {
      int toRead = (int)Math.Min(remaining, (uint)buf.Length);
      int read = this._stream.Read(buf, 0, toRead);
      if (read == 0)
        break;
      remaining -= (uint)read;
    }
  }

  private byte[] ReadExactBytes(int count) {
    byte[] buf = new byte[count];
    int totalRead = 0;
    while (totalRead < count) {
      int read = this._stream.Read(buf, totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream reading Spark data.");
      totalRead += read;
    }

    return buf;
  }

  private ushort ReadUInt16Le() {
    byte[] buf = ReadExactBytes(2);
    return (ushort)(buf[0] | (buf[1] << 8));
  }

  private uint ReadUInt32Le() {
    byte[] buf = ReadExactBytes(4);
    return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
  }

  private static string ReadNullTerminatedAscii(byte[] buffer) {
    int end = 0;
    while (end < buffer.Length && buffer[end] != 0)
      end++;
    return Encoding.ASCII.GetString(buffer, 0, end);
  }
}
