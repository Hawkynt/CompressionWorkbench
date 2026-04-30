using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Slf;

/// <summary>
/// Creates a Sir-Tech SLF library archive (Jagged Alliance 2 resource format).
/// </summary>
public sealed class SlfWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string _libName;
  private readonly string _libPath;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="SlfWriter"/>.
  /// </summary>
  /// <param name="stream">The destination stream.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="libName">The friendly library name written into the header (default: empty).</param>
  /// <param name="libPath">The virtual path prefix written into the header (default: empty).</param>
  public SlfWriter(Stream stream, bool leaveOpen = false, string libName = "", string libPath = "") {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._libName   = libName ?? "";
    this._libPath   = libPath ?? "";

    ValidateAsciiNameLength(this._libName, nameof(libName));
    ValidateAsciiNameLength(this._libPath, nameof(libPath));
  }

  /// <summary>
  /// Adds an entry to the archive.
  /// </summary>
  /// <param name="name">The entry path (ASCII, up to 255 bytes; the 256th byte is reserved for the null terminator).</param>
  /// <param name="data">The entry payload (stored uncompressed).</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ValidateAsciiNameLength(name, nameof(name));

    this._entries.Add((name, data));
  }

  /// <summary>
  /// Serializes the SLF library to the destination stream.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Payloads sit immediately after the entry table — caller can compute offsets up-front because
    // the header (532) and table size (count * 280) are both fixed.
    var dataStart = SlfConstants.HeaderSize + (long)this._entries.Count * SlfConstants.EntrySize;

    WriteHeader();
    var entryRecords = WriteEntryTablePlaceholderAndCollectOffsets(dataStart);
    WritePayloadsAndPatchOffsets(entryRecords);
  }

  private void WriteHeader() {
    Span<byte> header = stackalloc byte[SlfConstants.HeaderSize];
    header.Clear();

    WriteFixedAscii(header[..SlfConstants.NameFieldSize], this._libName);
    WriteFixedAscii(header.Slice(SlfConstants.NameFieldSize, SlfConstants.NameFieldSize), this._libPath);

    var fieldsBase = SlfConstants.NameFieldSize * 2;
    BinaryPrimitives.WriteInt32LittleEndian(header.Slice(fieldsBase, 4), this._entries.Count);
    // UsedEntries mirrors NumberOfEntries on a fresh write — we never emit tombstones.
    BinaryPrimitives.WriteInt32LittleEndian(header.Slice(fieldsBase + 4, 4), this._entries.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(fieldsBase + 8, 2), 0);                              // SortField
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(fieldsBase + 10, 2), SlfConstants.DefaultVersion);   // Version
    header[fieldsBase + 12] = 0;                                                                               // ContainsSubDirs
    // 3 bytes reserved + 4 bytes reserved are already zeroed by Clear().

    this._stream.Position = 0;
    this._stream.Write(header);
  }

  private long[] WriteEntryTablePlaceholderAndCollectOffsets(long dataStart) {
    var offsets = new long[this._entries.Count];
    var fileTime = DateTimeOffset.UtcNow.ToFileTime();

    Span<byte> entryBuf = stackalloc byte[SlfConstants.EntrySize];
    var cursor = dataStart;

    for (var i = 0; i < this._entries.Count; ++i) {
      entryBuf.Clear();
      var (name, data) = this._entries[i];
      offsets[i] = cursor;

      WriteFixedAscii(entryBuf[..SlfConstants.NameFieldSize], name);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(SlfConstants.NameFieldSize, 4), checked((uint)cursor));
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(SlfConstants.NameFieldSize + 4, 4), checked((uint)data.Length));
      // State byte distinguishes active (0x00) vs JA2-deleted (0xFF); we only ever emit active entries.
      entryBuf[SlfConstants.NameFieldSize + 8] = SlfConstants.StateActive;
      // FileTime is 64-bit because Windows FILETIME is 100-ns ticks since 1601 — fits Int64, not Int32.
      BinaryPrimitives.WriteInt64LittleEndian(entryBuf.Slice(SlfConstants.NameFieldSize + 12, 8), fileTime);

      this._stream.Write(entryBuf);
      cursor += data.Length;
    }

    return offsets;
  }

  private void WritePayloadsAndPatchOffsets(long[] offsets) {
    for (var i = 0; i < this._entries.Count; ++i) {
      var (_, data) = this._entries[i];
      // We pre-wrote offsets matching this exact sequential layout, so a plain append closes the loop.
      if (this._stream.Position != offsets[i])
        throw new InvalidOperationException($"SLF writer offset mismatch for entry {i}: expected {offsets[i]}, got {this._stream.Position}.");
      if (data.Length > 0)
        this._stream.Write(data);
    }
  }

  private static void WriteFixedAscii(Span<byte> destination, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    bytes.AsSpan().CopyTo(destination);
    // Remaining bytes are already zeroed by Clear().
  }

  private static void ValidateAsciiNameLength(string name, string paramName) {
    var byteLength = Encoding.ASCII.GetByteCount(name);
    if (byteLength > SlfConstants.MaxNameLength)
      throw new ArgumentException($"Name exceeds SLF max length of {SlfConstants.MaxNameLength} ASCII bytes (got {byteLength}).", paramName);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
