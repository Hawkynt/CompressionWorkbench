using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Reads entries from a cpio archive in the "new" (SVR4) ASCII format.
/// </summary>
public sealed class CpioReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="CpioReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the cpio archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Reads all entries from the archive.
  /// </summary>
  /// <returns>A list of entries with their associated data.</returns>
  public List<(CpioEntry Entry, byte[] Data)> ReadAll() {
    var result = new List<(CpioEntry, byte[])>();

    while (true) {
      var entry = ReadEntry(out var data);
      if (entry == null)
        break;
      result.Add((entry, data));
    }

    return result;
  }

  /// <summary>
  /// Reads the next entry from the archive.
  /// </summary>
  /// <param name="data">The entry's file data.</param>
  /// <returns>The entry, or null if the trailer was reached.</returns>
  public CpioEntry? ReadEntry(out byte[] data) {
    data = [];

    // Read the 110-byte fixed header
    var headerBuf = new byte[CpioConstants.NewAsciiHeaderSize];
    if (ReadExact(headerBuf) != headerBuf.Length)
      return null;

    var header = Encoding.ASCII.GetString(headerBuf);

    // Validate magic
    var magic = header[..6];
    if (magic != CpioConstants.NewAsciiMagic && magic != CpioConstants.NewCrcMagic)
      throw new InvalidDataException($"Invalid cpio magic: {magic}");

    var entry = new CpioEntry {
      Inode = ParseHex(header, 6, 8),
      Mode = ParseHex(header, 14, 8),
      Uid = ParseHex(header, 22, 8),
      Gid = ParseHex(header, 30, 8),
      NumLinks = ParseHex(header, 38, 8),
      ModificationTime = ParseHex(header, 46, 8),
      FileSize = ParseHex(header, 54, 8),
      DevMajor = ParseHex(header, 62, 8),
      DevMinor = ParseHex(header, 70, 8),
      RDevMajor = ParseHex(header, 78, 8),
      RDevMinor = ParseHex(header, 86, 8),
      Checksum = ParseHex(header, 102, 8),
    };

    var nameSize = (int)ParseHex(header, 94, 8);

    // Read filename
    var nameBuf = new byte[nameSize];
    ReadExact(nameBuf);

    // Name includes null terminator
    entry.Name = Encoding.ASCII.GetString(nameBuf, 0, nameSize > 0 ? nameSize - 1 : 0);

    // Align to 4-byte boundary after header + name
    var headerPlusName = CpioConstants.NewAsciiHeaderSize + nameSize;
    var namePadding = (4 - (headerPlusName % 4)) % 4;
    if (namePadding > 0)
      Skip(namePadding);

    // Check for trailer
    if (entry.Name == CpioConstants.Trailer)
      return null;

    // Read file data
    if (entry.FileSize > 0) {
      data = new byte[entry.FileSize];
      ReadExact(data);

      // Align to 4-byte boundary after data
      var dataPadding = (4 - (entry.FileSize % 4)) % 4;
      if (dataPadding > 0)
        Skip((int)dataPadding);
    }

    return entry;
  }

  private static uint ParseHex(string header, int offset, int length) {
    var hex = header.Substring(offset, length);
    return uint.Parse(hex, System.Globalization.NumberStyles.HexNumber);
  }

  private int ReadExact(byte[] buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer, totalRead, buffer.Length - totalRead);
      if (read == 0)
        return totalRead;
      totalRead += read;
    }
    return totalRead;
  }

  private void Skip(int count) {
    if (this._stream.CanSeek)
      this._stream.Position += count;
    else {
      var buf = new byte[count];
      ReadExact(buf);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
