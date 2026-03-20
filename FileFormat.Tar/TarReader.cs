using System.Text;

namespace FileFormat.Tar;

/// <summary>
/// Reads entries sequentially from a TAR archive.
/// </summary>
public sealed class TarReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;
  private TarEntry? _currentEntry;
  private long _remainingEntryBytes;
  private long _entryDataSize;
  private bool _needsSkipPadding;

  /// <summary>
  /// Initializes a new <see cref="TarReader"/> from a stream.
  /// </summary>
  /// <param name="stream">A stream containing the TAR archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public TarReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Reads the next entry from the archive.
  /// </summary>
  /// <returns>The next <see cref="TarEntry"/>, or <see langword="null"/> if the end of the archive has been reached.</returns>
  public TarEntry? GetNextEntry() {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    // Skip any remaining data from the previous entry
    SkipRemainingEntryData();

    // Read the header
    var entry = TarHeader.ReadHeader(this._stream, out bool isEndOfArchive);

    if (entry == null || isEndOfArchive) {
      // Try reading another block to confirm end-of-archive (two zero blocks)
      if (isEndOfArchive) {
        byte[] secondBlock = new byte[TarConstants.BlockSize];
        _ = this._stream.Read(secondBlock, 0, TarConstants.BlockSize);
      }

      this._currentEntry = null;
      this._remainingEntryBytes = 0;
      return null;
    }

    // Handle GNU long name extension
    if (entry.TypeFlag == TarConstants.TypeGnuLongName) {
      string longName = ReadEntryDataAsString(entry.Size);
      SkipPadding(entry.Size);

      // Read the next header and apply the long name
      var actualEntry = TarHeader.ReadHeader(this._stream, out _);
      if (actualEntry == null)
        throw new InvalidDataException("Expected entry header after GNU long name block.");

      actualEntry.Name = longName;
      entry = actualEntry;
    }
    // Handle GNU long link extension
    else if (entry.TypeFlag == TarConstants.TypeGnuLongLink) {
      string longLink = ReadEntryDataAsString(entry.Size);
      SkipPadding(entry.Size);

      // Read the next header and apply the long link
      var actualEntry = TarHeader.ReadHeader(this._stream, out _);
      if (actualEntry == null)
        throw new InvalidDataException("Expected entry header after GNU long link block.");

      actualEntry.LinkName = longLink;
      entry = actualEntry;
    }
    // Handle GNU multi-volume continuation
    else if (entry.TypeFlag == TarConstants.TypeGnuMultiVolume) {
      // Type 'M' entries have their data in this volume — expose them as regular files
      // with offset/realsize metadata so callers can reassemble the original file.
      entry.TypeFlag = TarConstants.TypeRegular;
    }
    // Handle PAX extended header
    else if (entry.TypeFlag == TarConstants.TypePaxHeader) {
      byte[] paxData = ReadEntryDataBytes(entry.Size);
      SkipPadding(entry.Size);
      var attributes = ParsePaxAttributes(paxData);

      // Read the next header and override fields
      var actualEntry = TarHeader.ReadHeader(this._stream, out _);
      if (actualEntry == null)
        throw new InvalidDataException("Expected entry header after PAX extended header.");

      ApplyPaxAttributes(actualEntry, attributes);
      entry = actualEntry;
    }

    this._currentEntry = entry;
    this._remainingEntryBytes = entry.Size;
    this._entryDataSize = entry.Size;
    this._needsSkipPadding = entry.Size > 0;
    return entry;
  }

  /// <summary>
  /// Returns a stream for reading the data of the current entry.
  /// </summary>
  /// <returns>A <see cref="Stream"/> limited to the current entry's data.</returns>
  public Stream GetEntryStream() {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._currentEntry == null)
      throw new InvalidOperationException("No current entry. Call GetNextEntry() first.");

    byte[] data = ReadEntryDataBytes(this._remainingEntryBytes);
    this._remainingEntryBytes = 0;

    // Skip padding to next 512-byte boundary after reading data
    if (this._needsSkipPadding) {
      SkipPadding(this._entryDataSize);
      this._needsSkipPadding = false;
    }

    return new MemoryStream(data, writable: false);
  }

  /// <summary>
  /// Skips past the data of the current entry.
  /// </summary>
  public void Skip() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    SkipRemainingEntryData();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private void SkipRemainingEntryData() {
    if (this._remainingEntryBytes > 0) {
      // Skip the remaining data bytes
      SkipBytes(this._remainingEntryBytes);
      this._remainingEntryBytes = 0;
    }

    // Skip padding to next 512-byte boundary
    if (this._needsSkipPadding) {
      SkipPadding(this._entryDataSize);
      this._needsSkipPadding = false;
    }
  }

  private void SkipPadding(long dataSize) {
    long padding = (TarConstants.BlockSize - (dataSize % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0)
      SkipBytes(padding);
  }

  private void SkipBytes(long count) {
    if (this._stream.CanSeek)
      this._stream.Position += count;
    else {
      byte[] buffer = new byte[Math.Min(count, 4096)];
      while (count > 0) {
        int toRead = (int)Math.Min(count, buffer.Length);
        int read = this._stream.Read(buffer, 0, toRead);
        if (read == 0) break;
        count -= read;
      }
    }
  }

  private string ReadEntryDataAsString(long size) {
    byte[] data = ReadEntryDataBytes(size);
    // Trim trailing null bytes
    var end = data.Length;
    while (end > 0 && data[end - 1] == 0)
      --end;
    return Encoding.UTF8.GetString(data, 0, end);
  }

  private byte[] ReadEntryDataBytes(long size) {
    if (size == 0)
      return [];

    byte[] data = new byte[size];
    var totalRead = 0;
    while (totalRead < size) {
      int read = this._stream.Read(data, totalRead, (int)(size - totalRead));
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of TAR data.");
      totalRead += read;
    }

    return data;
  }

  private static Dictionary<string, string> ParsePaxAttributes(byte[] data) {
    var attributes = new Dictionary<string, string>();
    var pos = 0;

    while (pos < data.Length) {
      // Format: "length key=value\n"
      int spaceIdx = Array.IndexOf(data, (byte)' ', pos);
      if (spaceIdx < 0) break;

      string lengthStr = Encoding.UTF8.GetString(data, pos, spaceIdx - pos);
      if (!int.TryParse(lengthStr, out int recordLength) || recordLength <= 0)
        break;

      int keyStart = spaceIdx + 1;
      int equalsIdx = Array.IndexOf(data, (byte)'=', keyStart);
      if (equalsIdx < 0) break;

      string key = Encoding.UTF8.GetString(data, keyStart, equalsIdx - keyStart);
      int valueStart = equalsIdx + 1;
      int valueEnd = pos + recordLength - 1; // -1 for the trailing newline
      if (valueEnd > data.Length) valueEnd = data.Length;

      string value = Encoding.UTF8.GetString(data, valueStart, valueEnd - valueStart);
      attributes[key] = value;

      pos += recordLength;
    }

    return attributes;
  }

  private static void ApplyPaxAttributes(TarEntry entry, Dictionary<string, string> attributes) {
    if (attributes.TryGetValue("path", out string? path))
      entry.Name = path;

    if (attributes.TryGetValue("linkpath", out string? linkPath))
      entry.LinkName = linkPath;

    if (attributes.TryGetValue("size", out string? sizeStr) && long.TryParse(sizeStr, out long size))
      entry.Size = size;

    if (attributes.TryGetValue("mtime", out string? mtimeStr) && double.TryParse(mtimeStr,
        System.Globalization.CultureInfo.InvariantCulture, out double mtime))
      entry.ModifiedTime = DateTimeOffset.FromUnixTimeSeconds((long)mtime);

    if (attributes.TryGetValue("uid", out string? uidStr) && int.TryParse(uidStr, out int uid))
      entry.Uid = uid;

    if (attributes.TryGetValue("gid", out string? gidStr) && int.TryParse(gidStr, out int gid))
      entry.Gid = gid;

    if (attributes.TryGetValue("uname", out string? uname))
      entry.UserName = uname;

    if (attributes.TryGetValue("gname", out string? gname))
      entry.GroupName = gname;
  }
}
