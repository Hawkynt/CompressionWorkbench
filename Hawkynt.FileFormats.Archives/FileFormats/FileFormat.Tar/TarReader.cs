using System.Buffers;
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
    var entry = TarHeader.ReadHeader(this._stream, out var isEndOfArchive);

    if (entry == null || isEndOfArchive) {
      // Try reading another block to confirm end-of-archive (two zero blocks)
      if (isEndOfArchive) {
        Span<byte> secondBlock = stackalloc byte[TarConstants.BlockSize];
        _ = this._stream.Read(secondBlock);
      }

      this._currentEntry = null;
      this._remainingEntryBytes = 0;
      return null;
    }

    // GNU long-name ('L') / long-link ('K') blocks and a PAX extended header ('x') each
    // describe the header that follows them, and they can be chained: GNU tar writes 'L'
    // then 'K' for a symlink whose name and target are both long.
    string? longName = null;
    string? longLink = null;
    List<KeyValuePair<string, string>>? paxAttributes = null;
    while (true) {
      string what;
      if (entry.TypeFlag == TarConstants.TypeGnuLongName) {
        longName = ReadEntryDataAsString(entry.Size);
        what = "GNU long name block";
      } else if (entry.TypeFlag == TarConstants.TypeGnuLongLink) {
        longLink = ReadEntryDataAsString(entry.Size);
        what = "GNU long link block";
      } else if (entry.TypeFlag == TarConstants.TypePaxHeader) {
        paxAttributes = ParsePaxAttributes(ReadEntryDataBytes(entry.Size));
        what = "PAX extended header";
      } else
        break;

      SkipPadding(entry.Size);
      entry = TarHeader.ReadHeader(this._stream, out _)
        ?? throw new InvalidDataException($"Expected entry header after {what}.");
    }

    if (longName != null) entry.Name = longName;
    if (longLink != null) entry.LinkName = longLink;
    if (paxAttributes != null) ApplyPaxAttributes(entry, paxAttributes);

    // Type 'M' entries have their data in this volume — expose them as regular files
    // with offset/realsize metadata so callers can reassemble the original file.
    if (entry.TypeFlag == TarConstants.TypeGnuMultiVolume)
      entry.TypeFlag = TarConstants.TypeRegular;

    // GNU old-style sparse: the map continues in 512-byte extension blocks.
    if (entry.TypeFlag == TarConstants.TypeGnuSparse) {
      this.ReadGnuSparseExtensions(entry);
      entry.TypeFlag = TarConstants.TypeRegular;
    }

    entry.StoredSize = entry.Size;
    this._currentEntry = entry;
    this._remainingEntryBytes = entry.Size;
    this._entryDataSize = entry.Size;
    this._needsSkipPadding = entry.Size > 0;

    // PAX sparse 1.0 keeps its map at the front of the member data.
    if (paxAttributes != null && entry.SparseMap == null && IsPaxSparse10(paxAttributes))
      this.ReadPaxSparse10Map(entry);

    if (entry.SparseMap != null) {
      ValidateSparseMap(entry);
      entry.Size = entry.RealSize;
    }
    return entry;
  }

  private void ReadGnuSparseExtensions(TarEntry entry) {
    var map = entry.SparseMap ??= [];
    var extended = entry.SparseHeaderExtended;
    Span<byte> block = stackalloc byte[TarConstants.BlockSize];
    const int mapBytes = TarHeader.GnuSparseExtensionEntries * 24;
    while (extended) {
      if (this._stream.ReadAtLeast(block, TarConstants.BlockSize, throwOnEndOfStream: false) < TarConstants.BlockSize)
        throw new InvalidDataException("TAR: truncated GNU sparse extension block.");
      TarHeader.ParseGnuSparseEntries(block[..mapBytes], map);
      extended = block[mapBytes] != 0;
    }
    entry.SparseHeaderExtended = false;
  }

  private static bool IsPaxSparse10(List<KeyValuePair<string, string>> attributes)
    => attributes.Any(a => a is { Key: "GNU.sparse.major", Value: "1" });

  /// <summary>
  /// Reads the PAX 1.0 sparse map: decimal numbers, one per line — the segment count,
  /// then offset and size per segment — padded to a block boundary, at the start of the data.
  /// </summary>
  private void ReadPaxSparse10Map(TarEntry entry) {
    var consumed = 0L;
    long ReadNumber() {
      long value = 0;
      var digits = 0;
      while (true) {
        if (consumed >= this._remainingEntryBytes)
          throw new InvalidDataException("TAR: PAX sparse 1.0 map runs past the member data.");
        var b = this._stream.ReadByte();
        if (b < 0) throw new EndOfStreamException("Unexpected end of TAR data.");
        ++consumed;
        if (b == '\n') break;
        if (b is < '0' or > '9' || ++digits > 18)
          throw new InvalidDataException("TAR: malformed number in PAX sparse 1.0 map.");
        value = value * 10 + (b - '0');
      }
      return digits == 0 ? throw new InvalidDataException("TAR: empty number in PAX sparse 1.0 map.") : value;
    }

    var count = ReadNumber();
    if (count > this._remainingEntryBytes / 4)
      throw new InvalidDataException("TAR: PAX sparse 1.0 segment count exceeds the member data.");
    var map = new List<TarSparseSegment>((int)count);
    for (var i = 0L; i < count; ++i)
      map.Add(new(ReadNumber(), ReadNumber()));

    var padding = (TarConstants.BlockSize - consumed % TarConstants.BlockSize) % TarConstants.BlockSize;
    if (consumed + padding > this._remainingEntryBytes)
      throw new InvalidDataException("TAR: PAX sparse 1.0 map runs past the member data.");
    this.SkipBytes(padding);
    this._remainingEntryBytes -= consumed + padding;
    entry.SparseMap = map;
    entry.StoredSize = this._remainingEntryBytes;
  }

  private static void ValidateSparseMap(TarEntry entry) {
    long end = 0, stored = 0;
    foreach (var (offset, length) in entry.SparseMap!) {
      if (offset < end || length < 0)
        throw new InvalidDataException("TAR: sparse map segments overlap or are out of order.");
      end = offset + length;
      stored += length;
    }
    if (stored != entry.StoredSize)
      throw new InvalidDataException($"TAR: sparse map describes {stored} stored bytes, member holds {entry.StoredSize}.");
    if (end > entry.RealSize)
      throw new InvalidDataException("TAR: sparse map extends past the real file size.");
  }

  /// <summary>
  /// Writes the current entry's logical content — stored segments with zero-filled
  /// holes for sparse members — to <paramref name="destination"/>.
  /// </summary>
  private void CopyLogicalData(Stream destination) {
    var entry = this._currentEntry!;
    if (entry.SparseMap is not { } map) {
      this.CopyStored(destination, this._remainingEntryBytes);
      return;
    }

    var position = 0L;
    foreach (var (offset, length) in map) {
      WriteHole(destination, offset - position);
      this.CopyStored(destination, length);
      position = offset + length;
    }
    WriteHole(destination, entry.RealSize - position);
  }

  private static void WriteHole(Stream destination, long count) {
    if (count <= 0)
      return;
    if (destination.CanSeek) {
      // Seeking past the end and extending the length reads back as zeros without writing them.
      var target = destination.Position + count;
      if (target > destination.Length)
        destination.SetLength(target);
      destination.Position = target;
      return;
    }
    var zeros = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 64 * 1024));
    try {
      Array.Clear(zeros);
      while (count > 0) {
        var n = (int)Math.Min(count, zeros.Length);
        destination.Write(zeros, 0, n);
        count -= n;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(zeros);
    }
  }

  private void CopyStored(Stream destination, long count) {
    if (count > this._remainingEntryBytes)
      throw new InvalidDataException("TAR: sparse segment runs past the member data.");
    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    try {
      while (count > 0) {
        var want = (int)Math.Min(buffer.Length, count);
        var read = this._stream.Read(buffer, 0, want);
        if (read <= 0) throw new EndOfStreamException("Unexpected end of TAR data.");
        destination.Write(buffer, 0, read);
        count -= read;
        this._remainingEntryBytes -= read;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <summary>
  /// Returns a stream for reading the data of the current entry.
  /// </summary>
  /// <returns>A <see cref="Stream"/> limited to the current entry's data.</returns>
  public Stream GetEntryStream() {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._currentEntry == null)
      throw new InvalidOperationException("No current entry. Call GetNextEntry() first.");

    byte[] data;
    var current = this._currentEntry;
    if (current.IsSparse) {
      if (current.RealSize > Array.MaxLength)
        throw new NotSupportedException($"TAR: sparse member '{current.Name}' expands to {current.RealSize} bytes; extract it to a file instead.");
      data = new byte[current.RealSize];
      using var buffer = new MemoryStream(data);
      this.CopyLogicalData(buffer);
    } else
      data = ReadEntryDataBytes(this._remainingEntryBytes);
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
    var padding = (TarConstants.BlockSize - (dataSize % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0)
      SkipBytes(padding);
  }

  private void SkipBytes(long count) {
    if (this._stream.CanSeek)
      this._stream.Position += count;
    else {
      var bufLen = (int)Math.Min(count, 4096);
      var buffer = ArrayPool<byte>.Shared.Rent(bufLen);
      try {
        while (count > 0) {
          var toRead = (int)Math.Min(count, bufLen);
          var read = this._stream.Read(buffer, 0, toRead);
          if (read == 0) break;
          count -= read;
        }
      } finally {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }
  }

  private string ReadEntryDataAsString(long size) {
    var data = ReadEntryDataBytes(size);
    // Trim trailing null bytes
    var end = data.Length;
    while (end > 0 && data[end - 1] == 0)
      --end;
    return Encoding.UTF8.GetString(data, 0, end);
  }

  /// <summary>
  /// Copies the current entry's data straight to <paramref name="destination" />
  /// without materialising it, then consumes the block padding. Required for
  /// entries larger than a byte[] can hold.
  /// </summary>
  public void CopyEntryDataTo(Stream destination) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(destination);
    if (this._currentEntry == null)
      throw new InvalidOperationException("No current entry. Call GetNextEntry() first.");

    this.CopyLogicalData(destination);
    this._remainingEntryBytes = 0;

    if (this._needsSkipPadding) {
      SkipPadding(this._entryDataSize);
      this._needsSkipPadding = false;
    }
  }

  private byte[] ReadEntryDataBytes(long size) {
    if (size == 0)
      return [];

    var data = new byte[size];
    var totalRead = 0;
    while (totalRead < size) {
      var read = this._stream.Read(data, totalRead, (int)(size - totalRead));
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of TAR data.");
      totalRead += read;
    }

    return data;
  }

  /// <summary>
  /// Parses PAX records in order. Order matters: sparse format 0.0 repeats the
  /// <c>GNU.sparse.offset</c> / <c>GNU.sparse.numbytes</c> keys once per segment.
  /// </summary>
  private static List<KeyValuePair<string, string>> ParsePaxAttributes(byte[] data) {
    var attributes = new List<KeyValuePair<string, string>>();
    var pos = 0;

    while (pos < data.Length) {
      // Format: "length key=value\n"
      var spaceIdx = Array.IndexOf(data, (byte)' ', pos);
      if (spaceIdx < 0) break;

      var lengthStr = Encoding.UTF8.GetString(data, pos, spaceIdx - pos);
      if (!int.TryParse(lengthStr, out var recordLength) || recordLength <= 0)
        break;

      var keyStart = spaceIdx + 1;
      var equalsIdx = Array.IndexOf(data, (byte)'=', keyStart);
      if (equalsIdx < 0) break;

      var key = Encoding.UTF8.GetString(data, keyStart, equalsIdx - keyStart);
      var valueStart = equalsIdx + 1;
      var valueEnd = pos + recordLength - 1; // -1 for the trailing newline
      if (valueEnd > data.Length) valueEnd = data.Length;

      var value = Encoding.UTF8.GetString(data, valueStart, valueEnd - valueStart);
      attributes.Add(new(key, value));

      pos += recordLength;
    }

    return attributes;
  }

  private static void ApplyPaxAttributes(TarEntry entry, List<KeyValuePair<string, string>> records) {
    // A re-declared key overrides the earlier record.
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (key, value) in records)
      attributes[key] = value;
    if (attributes.TryGetValue("path", out var path))
      entry.Name = path;

    if (attributes.TryGetValue("linkpath", out var linkPath))
      entry.LinkName = linkPath;

    if (attributes.TryGetValue("size", out var sizeStr) && long.TryParse(sizeStr, out var size))
      entry.Size = size;

    if (attributes.TryGetValue("mtime", out var mtimeStr) && double.TryParse(mtimeStr,
        System.Globalization.CultureInfo.InvariantCulture, out var mtime))
      entry.ModifiedTime = DateTimeOffset.FromUnixTimeSeconds((long)mtime);

    if (attributes.TryGetValue("uid", out var uidStr) && int.TryParse(uidStr, out var uid))
      entry.Uid = uid;

    if (attributes.TryGetValue("gid", out var gidStr) && int.TryParse(gidStr, out var gid))
      entry.Gid = gid;

    if (attributes.TryGetValue("uname", out var uname))
      entry.UserName = uname;

    if (attributes.TryGetValue("gname", out var gname))
      entry.GroupName = gname;

    ApplyPaxSparse(entry, records, attributes);
  }

  /// <summary>
  /// GNU tar's three PAX sparse encodings: 0.0 (repeated offset/numbytes records),
  /// 0.1 (one comma-separated <c>GNU.sparse.map</c>) and 1.0 (map in the data, read later).
  /// All carry the real size in <c>GNU.sparse.size</c> or <c>GNU.sparse.realsize</c>, and
  /// 0.1/1.0 the real name in <c>GNU.sparse.name</c>.
  /// </summary>
  private static void ApplyPaxSparse(TarEntry entry, List<KeyValuePair<string, string>> records, Dictionary<string, string> attributes) {
    var realSizeText = attributes.GetValueOrDefault("GNU.sparse.realsize") ?? attributes.GetValueOrDefault("GNU.sparse.size");
    if (realSizeText == null)
      return;
    entry.RealSize = ParseSparseNumber(realSizeText);
    if (attributes.TryGetValue("GNU.sparse.name", out var sparseName))
      entry.Name = sparseName;

    if (attributes.TryGetValue("GNU.sparse.map", out var mapText)) {
      var numbers = mapText.Length == 0 ? [] : mapText.Split(',').Select(ParseSparseNumber).ToArray();
      if (numbers.Length % 2 != 0)
        throw new InvalidDataException("TAR: GNU.sparse.map has an odd number of values.");
      entry.SparseMap = [];
      for (var i = 0; i < numbers.Length; i += 2)
        entry.SparseMap.Add(new(numbers[i], numbers[i + 1]));
      return;
    }

    if (!attributes.ContainsKey("GNU.sparse.offset"))
      return; // format 1.0: the map is read from the member data

    entry.SparseMap = [];
    long? pendingOffset = null;
    foreach (var (key, value) in records) {
      if (key == "GNU.sparse.offset")
        pendingOffset = ParseSparseNumber(value);
      else if (key == "GNU.sparse.numbytes") {
        if (pendingOffset is not { } offset)
          throw new InvalidDataException("TAR: GNU.sparse.numbytes without a preceding GNU.sparse.offset.");
        entry.SparseMap.Add(new(offset, ParseSparseNumber(value)));
        pendingOffset = null;
      }
    }
  }

  private static long ParseSparseNumber(string text)
    => long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
      ? v
      : throw new InvalidDataException($"TAR: malformed GNU sparse number '{text}'.");
}
