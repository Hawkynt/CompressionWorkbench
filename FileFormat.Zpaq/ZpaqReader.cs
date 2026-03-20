using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// Reads the journaling index of a ZPAQ archive, exposing the files recorded
/// across all transactions.
/// </summary>
/// <remarks>
/// ZPAQ archives contain a ZPAQL virtual-machine program that is required to
/// decompress the payload.  This reader parses only the journaling structure
/// (block headers, transaction metadata) to enumerate entries and their
/// attributes.  Decompression is not supported; <see cref="Extract"/> always
/// throws <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class ZpaqReader : IDisposable {
  private readonly Stream         _stream;
  private readonly bool           _leaveOpen;
  private readonly List<ZpaqEntry> _entries = [];
  private bool                    _disposed;

  /// <summary>
  /// Gets the entries discovered by scanning the archive journal.
  /// The list reflects the last recorded state of each file across all
  /// transactions (i.e. later transactions that mention the same filename
  /// supersede earlier ones in the final view).
  /// </summary>
  public IReadOnlyList<ZpaqEntry> Entries => _entries;

  // ── Construction ─────────────────────────────────────────────────────────

  /// <summary>
  /// Opens a ZPAQ archive stream and scans its journal.
  /// </summary>
  /// <param name="stream">
  /// A readable stream positioned at the beginning of a ZPAQ archive.
  /// The stream does not need to be seekable; the reader performs a single
  /// forward scan.
  /// </param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open when
  /// this reader is disposed; <see langword="false"/> (default) to dispose it.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="stream"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="stream"/> is not readable.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the stream contains no recognizable ZPAQ blocks.
  /// </exception>
  public ZpaqReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("Stream must be readable.", nameof(stream));

    _stream    = stream;
    _leaveOpen = leaveOpen;

    ScanJournal();
  }

  // ── Journal scanning ─────────────────────────────────────────────────────

  /// <summary>
  /// Scans the stream for ZPAQ blocks and builds the entry list from any
  /// header ('c') blocks whose content is accessible (uncompressed or stored).
  /// </summary>
  private void ScanJournal() {
    // We maintain a byte-level sliding window to detect the 3-byte "zPQ"
    // prefix even when the stream is not seekable.
    // For each block we find:
    //   • record the block's raw byte span (for CompressedSize accounting)
    //   • if it is a header ('c') block, attempt to parse file metadata
    //   • group blocks by transaction number

    var raw      = new BufferedStream(_stream, 65536);
    var rawBytes = new RawByteReader(raw);

    // Map from filename → latest entry (journaling: last write wins).
    var latestByName = new Dictionary<string, ZpaqEntry>(StringComparer.Ordinal);
    // Cumulative data-block bytes associated with the current transaction.
    long txDataBytes    = 0;
    int  txVersion      = 0;
    // Names collected from the current transaction's header block.
    var  txNames        = new List<(string Name, long Size, DateTime? Modified)>();
    // Running block-start position (approximate; only accurate when seekable).
    long blockStart     = 0;
    bool foundAnyBlock  = false;

    while (true) {
      // Scan forward until we find "zPQ".
      if (!rawBytes.ScanToPrefix(ZpaqConstants.BlockPrefix))
        break;

      foundAnyBlock = true;
      blockStart    = rawBytes.Position - 3; // prefix length

      // Read level byte and block-type byte.
      int lvl  = rawBytes.ReadByte();
      if (lvl < 0) break;
      int type = rawBytes.ReadByte();
      if (type < 0) break;

      switch ((byte)type) {
        case ZpaqConstants.BlockTypeHeader:
          // 'c' block — start of a new transaction.
          // Flush the previous transaction into the entry list.
          FlushTransaction(latestByName, txNames, txDataBytes, txVersion);

          txVersion++;
          txDataBytes = 0;
          txNames.Clear();

          // A header block in a level-1 archive that was created with
          // "zpaq add" uses stored (uncompressed) content so that the
          // journal can be scanned without decompressing.  The block
          // payload immediately follows the 5-byte block header and is
          // terminated by a 0xFF byte (end-of-block marker in ZPAQ1).
          ParseHeaderBlockPayload(rawBytes, txNames);
          break;

        case ZpaqConstants.BlockTypeData:
          // 'd' block — data payload.  Measure its size.
          txDataBytes += MeasureBlockPayload(rawBytes);
          break;

        case ZpaqConstants.BlockTypeIndex:
          // 'h' block — transaction close / hash block.  Flush.
          FlushTransaction(latestByName, txNames, txDataBytes, txVersion);
          txVersion++;
          txDataBytes = 0;
          txNames.Clear();
          SkipBlockPayload(rawBytes);
          break;

        case ZpaqConstants.BlockTypeCompressed:
        default:
          // Unknown or compressed data block — skip.
          SkipBlockPayload(rawBytes);
          break;
      }
    }

    // Flush any trailing transaction that had no 'h' block.
    FlushTransaction(latestByName, txNames, txDataBytes, txVersion);

    if (!foundAnyBlock)
      ThrowInvalidData("No ZPAQ blocks found in stream.");

    // Build the final entry list: latest state of each file.
    _entries.AddRange(latestByName.Values
      .OrderBy(e => e.FileName, StringComparer.Ordinal));
  }

  // ── Header block parsing ─────────────────────────────────────────────────

  /// <summary>
  /// Attempts to parse the content of a ZPAQ level-1 journaling 'c' block.
  ///
  /// A stored (not-ZPAQL-compressed) 'c' block has the layout:
  ///   8 bytes  : Windows FILETIME of the transaction date/time
  ///   For each file in the transaction:
  ///     1 byte   : attribute flags (0x20 = directory on Windows,
  ///                                 0x10 = directory on Unix)
  ///     variable : null-terminated UTF-8 filename
  ///     8 bytes  : uncompressed file size (little-endian int64)
  ///   0xFF       : end-of-block marker
  ///
  /// If the first byte of the payload is NOT a valid attribute byte
  /// (i.e. it is 0xFF, meaning an immediately empty block, or it
  /// indicates a ZPAQL program header) we fall through gracefully.
  /// </summary>
  private static void ParseHeaderBlockPayload(
      RawByteReader reader,
      List<(string Name, long Size, DateTime? Modified)> names) {

    // The block payload may be preceded by a 2-byte ZPAQL program length
    // (big-endian) in ZPAQ level-1 compressed blocks.  In journaling archives
    // created by the reference `zpaq` tool the 'c' block content is stored
    // verbatim (not ZPAQL-compressed) and starts immediately with the
    // 8-byte Windows FILETIME.

    // Read the 8-byte transaction timestamp (Windows FILETIME, little-endian).
    Span<byte> tsBytes = stackalloc byte[8];
    if (!reader.TryReadBytes(tsBytes))
      return; // truncated — give up

    long ft = BitConverter.ToInt64(tsBytes);
    DateTime? txDate = DecodeWindowsFileTime(ft);

    // Read file entries until we hit 0xFF (end-of-block).
    Span<byte> szBytes = stackalloc byte[8]; // moved outside loop to satisfy CA2014
    while (true) {
      int attr = reader.ReadByte();
      if (attr < 0 || attr == 0xFF)
        break; // EOF or end-of-block marker

      // Read null-terminated UTF-8 filename.
      string name = reader.ReadNullTerminatedString();
      if (name.Length == 0) {
        // Skip the 8-byte size field and continue.
        reader.Skip(8);
        continue;
      }

      // Read 8-byte uncompressed size.
      if (!reader.TryReadBytes(szBytes))
        break;
      long size = BitConverter.ToInt64(szBytes);

      names.Add((name, size, txDate));
    }
  }

  // ── Block payload helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Measures the byte length of the current block payload by scanning
  /// forward to the next "zPQ" prefix (or end of stream) without consuming
  /// the prefix itself.  Returns the number of payload bytes consumed.
  /// </summary>
  private static long MeasureBlockPayload(RawByteReader reader) {
    long start = reader.Position;
    reader.SkipToNextBlock();
    return reader.Position - start;
  }

  /// <summary>
  /// Skips the current block payload, positioning the reader just before
  /// the next "zPQ" prefix (or at end of stream).
  /// </summary>
  private static void SkipBlockPayload(RawByteReader reader) =>
    reader.SkipToNextBlock();

  // ── Transaction flushing ─────────────────────────────────────────────────

  private static void FlushTransaction(
      Dictionary<string, ZpaqEntry>                       dest,
      List<(string Name, long Size, DateTime? Modified)>  names,
      long                                                 dataBytes,
      int                                                  version) {
    if (names.Count == 0)
      return;

    // Distribute the data-block bytes equally across files in the transaction
    // as a rough approximation (ZPAQ doesn't store per-file compressed sizes).
    long perFile = names.Count > 0 ? dataBytes / names.Count : 0;

    foreach (var (name, size, modified) in names) {
      bool isDir = name.EndsWith('/') || name.EndsWith('\\');
      var entry = new ZpaqEntry(
        fileName:       NormalizeName(name),
        size:           size,
        compressedSize: isDir ? 0 : perFile,
        lastModified:   modified,
        isDirectory:    isDir,
        version:        version);

      dest[entry.FileName] = entry; // later transaction supersedes earlier
    }
  }

  // ── Utilities ─────────────────────────────────────────────────────────────

  private static string NormalizeName(string name) =>
    name.Replace('\\', '/');

  private static DateTime? DecodeWindowsFileTime(long ft) {
    if (ft <= 0)
      return null;

    try {
      long ticks = ft - ZpaqConstants.WindowsToUnixEpochTicks;
      if (ticks < 0)
        return null;
      return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        .AddTicks(ticks);
    } catch {
      return null;
    }
  }

  private static void ThrowInvalidData(string message) =>
    throw new InvalidDataException(message);

  // ── Extract (not supported) ───────────────────────────────────────────────

  /// <summary>
  /// Not supported.  ZPAQ decompression requires executing a ZPAQL virtual
  /// machine program embedded in each archive block.
  /// </summary>
  /// <exception cref="NotSupportedException">Always thrown.</exception>
  public Stream Extract(ZpaqEntry entry) =>
    throw new NotSupportedException(
      "ZPAQ decompression requires a ZPAQL virtual machine which is not implemented.");

  // ── IDisposable ───────────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Dispose() {
    if (!_disposed) {
      _disposed = true;
      if (!_leaveOpen)
        _stream.Dispose();
    }
  }
}

// ── Internal streaming helper ─────────────────────────────────────────────────

/// <summary>
/// A forward-only, low-allocation byte reader that supports the scanning
/// operations needed by <see cref="ZpaqReader"/>.
/// </summary>
internal sealed class RawByteReader {
  private readonly Stream  _stream;
  private readonly byte[]  _buf;
  private int              _pos;   // read position within _buf
  private int              _len;   // valid bytes in _buf
  private long             _streamOffset; // bytes consumed from stream so far

  // The "zPQ" prefix as a cached value.
  private static ReadOnlySpan<byte> Prefix => ZpaqConstants.BlockPrefix;
  private const int PrefixLen = 3;

  // Lookahead window — we keep the last (PrefixLen - 1) bytes of each chunk
  // in the buffer overlap so we never miss a prefix that straddles a chunk
  // boundary.
  private const int BufSize = 65536;
  private const int Overlap = PrefixLen - 1; // = 2

  internal RawByteReader(Stream stream) {
    _stream = stream;
    _buf    = new byte[BufSize + Overlap];
    _pos    = Overlap; // first Overlap bytes reserved for tail-of-previous-chunk
    _len    = Overlap; // nothing real yet
  }

  /// <summary>Gets the number of bytes consumed from the stream so far.</summary>
  internal long Position => _streamOffset - (_len - _pos);

  // ── Primitive I/O ────────────────────────────────────────────────────────

  /// <summary>Returns the next byte or -1 on end-of-stream.</summary>
  internal int ReadByte() {
    if (!EnsureAvailable(1))
      return -1;
    return _buf[_pos++];
  }

  /// <summary>
  /// Reads exactly <paramref name="dest"/>.Length bytes into <paramref name="dest"/>.
  /// Returns <see langword="false"/> if fewer bytes are available.
  /// </summary>
  internal bool TryReadBytes(Span<byte> dest) {
    int need = dest.Length;
    if (!EnsureAvailable(need))
      return false;

    _buf.AsSpan(_pos, need).CopyTo(dest);
    _pos += need;
    return true;
  }

  /// <summary>Skips exactly <paramref name="count"/> bytes.</summary>
  internal void Skip(int count) {
    while (count > 0) {
      if (!EnsureAvailable(1))
        return;
      int can = Math.Min(count, _len - _pos);
      _pos  += can;
      count -= can;
    }
  }

  // ── Null-terminated string ────────────────────────────────────────────────

  internal string ReadNullTerminatedString() {
    var sb = new StringBuilder(32);
    while (true) {
      int b = ReadByte();
      if (b <= 0) // EOF or null terminator
        break;
      sb.Append((char)b);
    }
    return sb.ToString();
  }

  // ── Block-boundary scanning ───────────────────────────────────────────────

  /// <summary>
  /// Scans forward from the current position until the 3-byte "zPQ" prefix
  /// is found.  On return the stream position is just after the prefix.
  /// Returns <see langword="true"/> if the prefix was found; <see langword="false"/>
  /// if end-of-stream was reached first.
  /// </summary>
  internal bool ScanToPrefix(ReadOnlySpan<byte> prefix) {
    // Sliding 3-byte window (w[0]..w[2]).  We advance one byte at a time until
    // the window matches the 3-byte prefix.  Using explicit state avoids the
    // edge case where 0x00 is valid data and cannot be used as a sentinel.
    //
    // State: how many leading bytes of the prefix we have matched so far.
    int matched = 0;

    while (true) {
      int b = ReadByte();
      if (b < 0)
        return false; // end-of-stream

      if (b == prefix[matched]) {
        matched++;
        if (matched == prefix.Length)
          return true; // full prefix found; stream positioned after it
      } else {
        // Mismatch — restart, but check whether this byte starts the prefix.
        matched = (b == prefix[0]) ? 1 : 0;
      }
    }
  }

  /// <summary>
  /// Advances past the current block's payload, stopping just before the
  /// next "zPQ" block prefix, or at end-of-stream.
  /// The prefix itself is NOT consumed.
  /// </summary>
  internal void SkipToNextBlock() {
    ReadOnlySpan<byte> prefix = Prefix;
    // We need to stop before consuming the prefix, so we look-ahead:
    // scan for 'z' then check the following two bytes.

    while (true) {
      if (!EnsureAvailable(PrefixLen))
        return; // EOF — nothing more to skip

      int available = _len - _pos;
      var window = _buf.AsSpan(_pos, available);

      for (int i = 0; i < window.Length; i++) {
        if (window[i] != prefix[0])
          continue;

        // Potential match — need two more bytes.
        if (i + PrefixLen - 1 >= window.Length) {
          // Not enough in buffer — advance to i and refill.
          _pos += i;
          goto refill;
        }

        if (window[i + 1] == prefix[1] && window[i + 2] == prefix[2]) {
          // Found next prefix — stop here without consuming it.
          _pos += i;
          return;
        }
      }

      _pos = _len; // consume the whole window
      refill:
      Refill();
      if (_len == _pos)
        return;
    }
  }

  // ── Buffer management ─────────────────────────────────────────────────────

  private bool EnsureAvailable(int need) {
    if (_len - _pos >= need)
      return true;
    Refill();
    return _len - _pos >= need;
  }

  private void Refill() {
    // Copy the tail of the current buffer into the overlap area.
    int tail = Math.Min(_len - _pos, Overlap);
    if (tail > 0)
      _buf.AsSpan(_len - tail, tail).CopyTo(_buf.AsSpan(Overlap - tail, tail));

    _pos = Overlap - tail; // new read position in the buffer
    int read = _stream.Read(_buf, Overlap, BufSize);
    _len = Overlap + read;
    _streamOffset += read;
  }
}
