using System.IO.Compression;
using System.Text;

namespace FileFormat.StuffItX;

/// <summary>
/// Reads entries from a StuffIt X (.sitx) archive.
/// </summary>
/// <remarks>
/// <para>
/// StuffIt X uses a hierarchical, element-based container format introduced by Aladdin Systems
/// (later Smith Micro Software). This implementation supports:
/// <list type="bullet">
///   <item>Magic validation ("StuffIt!" or "StuffIt" + version byte).</item>
///   <item>P2 variable-length integer decoding (7 data bits per byte, big-endian, MSB = continuation).</item>
///   <item>Sequential element-stream parsing to build a flat entry list.</item>
///   <item>Extraction of stored entries (method 0) and Deflate-compressed entries (method 5).</item>
///   <item>Graceful skip of all other compression methods — entries are still listed.</item>
/// </list>
/// </para>
/// <para>
/// Compression method codes: 0 = Stored, 2 = Brimstone (PPMd variant), 5 = Deflate,
/// 8 = Darkhorse (LZSS), 13 = Cyanide (BWT), 14 = Iron (BWT).
/// </para>
/// </remarks>
public sealed class StuffItXReader : IDisposable {
  // ── Magic / header constants ──────────────────────────────────────────────────

  // "StuffIt!" as ASCII bytes
  private static readonly byte[] MagicFull = "StuffIt!"u8.ToArray();
  // "StuffIt" prefix (7 bytes) — version byte follows
  private static readonly byte[] MagicPrefix = "StuffIt"u8.ToArray();

  // Minimum header size before the element stream.
  // The exact offset is read from the header; we require at least 0x60 bytes.
  private const int MinHeaderSize = 0x60;

  // ── Element type tags (P2-encoded, low bits) ─────────────────────────────────

  // These are representative tag values observed in the SITX format.
  // The lower nibble of the tag often encodes the element subtype.
  private const long TagEndOfStream       = 0x00;
  private const long TagDirectoryStart    = 0x20;
  private const long TagDirectoryEnd      = 0x21;
  private const long TagFile              = 0x30;
  private const long TagDataFork         = 0x40;
  private const long TagResourceFork     = 0x41;

  // Compression method codes
  private const int MethodStored  = 0;
  private const int MethodDeflate = 5;

  // ── State ─────────────────────────────────────────────────────────────────────

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<StuffItXEntry> _entries = [];
  private bool _disposed;

  // ── Public API ────────────────────────────────────────────────────────────────

  /// <summary>
  /// Opens a StuffIt X archive from the given stream and parses its element catalog.
  /// </summary>
  /// <param name="stream">A seekable, readable stream positioned at offset 0.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open when this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not begin with a valid StuffIt X magic.</exception>
  public StuffItXReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    ParseArchive();
  }

  /// <summary>Gets the list of entries discovered during parsing.</summary>
  public IReadOnlyList<StuffItXEntry> Entries => this._entries;

  /// <summary>
  /// Extracts and decompresses the data for the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The decompressed entry bytes.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when the entry uses a compression method that is not supported for extraction.
  /// </exception>
  public byte[] Extract(StuffItXEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.CompressedSize == 0 && entry.OriginalSize == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    var compressed = ReadExact((int)entry.CompressedSize);

    return entry.MethodCode switch {
      MethodStored  => compressed,
      MethodDeflate => DecompressDeflate(compressed, (int)entry.OriginalSize),
      _             => throw new NotSupportedException(
                         $"StuffIt X compression method {entry.MethodCode} ({entry.Method}) " +
                         "is not supported for extraction."),
    };
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Parsing ───────────────────────────────────────────────────────────────────

  private void ParseArchive() {
    if (this._stream.Length < MinHeaderSize)
      throw new InvalidDataException("Stream is too short to be a StuffIt X archive.");

    this._stream.Position = 0;
    var headerBytes = ReadExact(MinHeaderSize);

    ValidateMagic(headerBytes);

    // Bytes 0x58–0x5B (offsets 88–91): element stream start offset (big-endian uint32).
    // This field points to where the element catalog begins in the file.
    // Different .sitx versions place the offset at slightly different positions;
    // we try 0x28 first (a common location), then fall back to scanning.
    var elementOffset = FindElementStreamOffset(headerBytes);

    if (elementOffset <= 0 || elementOffset >= this._stream.Length)
      elementOffset = MinHeaderSize; // fall back to end-of-header

    this._stream.Position = elementOffset;
    ParseElementStream();
  }

  private static void ValidateMagic(byte[] header) {
    // Check for "StuffIt!" (8-byte full magic)
    var fullMatch = true;
    for (var i = 0; i < MagicFull.Length && i < header.Length; i++) {
      if (header[i] != MagicFull[i]) { fullMatch = false; break; }
    }
    if (fullMatch) return;

    // Check for "StuffIt" prefix (7 bytes) — version byte follows at offset 7
    var prefixMatch = true;
    for (var i = 0; i < MagicPrefix.Length && i < header.Length; i++) {
      if (header[i] != MagicPrefix[i]) { prefixMatch = false; break; }
    }
    if (prefixMatch) return;

    throw new InvalidDataException(
      "Not a StuffIt X archive: magic bytes do not match \"StuffIt\" prefix.");
  }

  /// <summary>
  /// Heuristically locates the element-stream start offset from the archive header.
  /// StuffIt X stores a pointer in the header at a version-dependent position.
  /// We try several known positions before falling back.
  /// </summary>
  private static long FindElementStreamOffset(byte[] header) {
    // Candidate locations for the "catalog offset" pointer (big-endian uint32).
    // These come from reverse-engineering notes and community documentation.
    int[] candidateOffsets = [0x28, 0x30, 0x38, 0x54, 0x58];

    foreach (var pos in candidateOffsets) {
      if (pos + 4 > header.Length) continue;
      var offset = ReadBigEndianUInt32(header, pos);
      // A plausible offset is > MinHeaderSize and < some large limit
      if (offset >= MinHeaderSize && offset < 0x10000000)
        return offset;
    }

    return MinHeaderSize;
  }

  private void ParseElementStream() {
    // Directory stack tracks the current path as we descend into directories.
    var dirStack = new Stack<string>();

    var safetyLimit = this._stream.Length;
    var iterations  = 0;

    while (this._stream.Position < safetyLimit && iterations < 100_000) {
      iterations++;
      var startPos = this._stream.Position;

      long tag;
      long elementDataSize;
      try {
        tag             = ReadP2(this._stream);
        elementDataSize = ReadP2(this._stream);
      } catch (EndOfStreamException) {
        break;
      }

      if (tag == TagEndOfStream && elementDataSize == 0)
        break;

      var elementDataStart = this._stream.Position;

      try {
        ProcessElement(tag, elementDataSize, elementDataStart, dirStack);
      } catch (Exception) {
        // On any parse error inside an element, skip the element body and continue.
      }

      // Always advance to the next element regardless of what ProcessElement did.
      var nextPos = elementDataStart + elementDataSize;
      if (nextPos <= this._stream.Position && elementDataSize > 0) {
        // ProcessElement consumed more than the element — something is wrong; stop.
        break;
      }
      if (nextPos > this._stream.Length)
        break;
      this._stream.Position = nextPos;

      // Guard against zero-progress loops.
      if (this._stream.Position == startPos)
        break;
    }
  }

  private void ProcessElement(long tag, long elementDataSize, long elementDataStart,
                               Stack<string> dirStack) {
    if (elementDataSize <= 0) return;

    switch (tag) {
      case TagDirectoryStart: {
        var name = ReadP2String(elementDataSize);
        dirStack.Push(name);
        var path = BuildPath(dirStack);
        this._entries.Add(new StuffItXEntry {
          Name         = name,
          FullPath     = path,
          IsDirectory  = true,
          OriginalSize = 0,
          CompressedSize = 0,
          Method       = "",
          MethodCode   = 0,
        });
        break;
      }

      case TagDirectoryEnd:
        if (dirStack.Count > 0)
          dirStack.Pop();
        break;

      case TagFile: {
        ParseFileElement(elementDataSize, elementDataStart, dirStack);
        break;
      }

      default:
        // Unknown or unsupported element — skip (caller will advance stream).
        break;
    }
  }

  private void ParseFileElement(long elementDataSize, long elementDataStart,
                                 Stack<string> dirStack) {
    if (elementDataSize < 4) return;

    // File element layout (simplified):
    //   P2  name length + name bytes
    //   P2  method code
    //   P2  original (uncompressed) size
    //   P2  compressed size
    //   ... compressed data follows (may be inline or pointed to by offset)

    var name           = ReadP2String(elementDataSize);
    if (string.IsNullOrEmpty(name)) return;

    var methodCode     = (int)ReadP2(this._stream);
    var originalSize   = ReadP2(this._stream);
    var compressedSize = ReadP2(this._stream);

    // Remaining bytes in the element body are the compressed data.
    var dataOffset  = this._stream.Position;
    var headerConsumed = dataOffset - elementDataStart;
    var remaining   = elementDataSize - headerConsumed;

    // Validate sizes: if compressedSize is unreasonable, derive from remaining bytes.
    if (compressedSize <= 0 || compressedSize > remaining)
      compressedSize = remaining > 0 ? remaining : 0;

    var fullPath = BuildChildPath(dirStack, name);
    var methodName = MethodName(methodCode);

    this._entries.Add(new StuffItXEntry {
      Name           = name,
      FullPath       = fullPath,
      IsDirectory    = false,
      OriginalSize   = originalSize,
      CompressedSize = compressedSize,
      Method         = methodName,
      MethodCode     = methodCode,
      DataOffset     = dataOffset,
    });
  }

  // ── String reading ────────────────────────────────────────────────────────────

  /// <summary>
  /// Reads a P2-length-prefixed string from the current stream position.
  /// Falls back to empty string on any error or if the length exceeds the element budget.
  /// </summary>
  private string ReadP2String(long elementBudget) {
    var len = ReadP2(this._stream);
    if (len <= 0 || len > elementBudget || len > 4096)
      return "";
    var buf = ReadExact((int)len);
    return Encoding.UTF8.GetString(buf);
  }

  // ── Path helpers ──────────────────────────────────────────────────────────────

  private static string BuildPath(Stack<string> dirStack) {
    // Stack enumeration is LIFO — reverse to get top-down order.
    var parts = dirStack.ToArray();
    Array.Reverse(parts);
    return string.Join("/", parts) + "/";
  }

  private static string BuildChildPath(Stack<string> dirStack, string name) {
    if (dirStack.Count == 0) return name;
    var parts = dirStack.ToArray();
    Array.Reverse(parts);
    return string.Join("/", parts) + "/" + name;
  }

  // ── Decompression ─────────────────────────────────────────────────────────────

  private static byte[] DecompressDeflate(byte[] compressed, int expectedSize) {
    using var input  = new MemoryStream(compressed);
    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
    if (expectedSize > 0) {
      var buf  = new byte[expectedSize];
      var total = 0;
      while (total < expectedSize) {
        var read = deflate.Read(buf, total, expectedSize - total);
        if (read == 0) break;
        total += read;
      }
      return buf;
    }
    using var output = new MemoryStream();
    deflate.CopyTo(output);
    return output.ToArray();
  }

  // ── P2 variable-length integer ────────────────────────────────────────────────

  /// <summary>
  /// Reads a P2 variable-length integer from <paramref name="s"/>.
  /// Each byte contributes 7 data bits (big-endian); the high bit signals continuation.
  /// </summary>
  private static long ReadP2(Stream s) {
    long val = 0;
    while (true) {
      var b = s.ReadByte();
      if (b < 0) throw new EndOfStreamException("Unexpected end of stream reading P2 integer.");
      val = (val << 7) | (b & 0x7FL);
      if ((b & 0x80) == 0) break;
    }
    return val;
  }

  // ── Stream helpers ────────────────────────────────────────────────────────────

  private byte[] ReadExact(int count) {
    if (count <= 0) return [];
    var buf   = new byte[count];
    var total = 0;
    while (total < buf.Length) {
      var read = this._stream.Read(buf, total, buf.Length - total);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream reading StuffIt X data.");
      total += read;
    }
    return buf;
  }

  private static uint ReadBigEndianUInt32(byte[] buf, int offset)
    => (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) |
              (buf[offset + 2] << 8)  |  buf[offset + 3]);

  // ── Method name lookup ────────────────────────────────────────────────────────

  private static string MethodName(int code) => code switch {
    0  => "Stored",
    2  => "Brimstone",
    5  => "Deflate",
    8  => "Darkhorse",
    13 => "Cyanide",
    14 => "Iron",
    _  => $"Method{code}",
  };
}
