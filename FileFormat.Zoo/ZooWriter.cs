using System.Text;
using Compression.Core.Checksums;
using Compression.Core.BitIO;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Zoo;

/// <summary>
/// Creates a Zoo archive.
/// </summary>
public sealed class ZooWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly ZooCompressionMethod _defaultMethod;

  // Pending entries: entry metadata and compressed bytes.
  private readonly List<(ZooEntry Entry, byte[] CompressedData)> _pending = [];

  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="ZooWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the Zoo archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <param name="defaultMethod">The default compression method for added entries.</param>
  public ZooWriter(
    Stream stream,
    bool leaveOpen = false,
    ZooCompressionMethod defaultMethod = ZooCompressionMethod.Lzw) {
    this._stream        = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen     = leaveOpen;
    this._defaultMethod = defaultMethod;

    WriteArchiveHeader();
  }

  // ── Writing ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a file entry to the archive.
  /// </summary>
  /// <param name="fileName">
  /// The filename to store.  Names longer than <see cref="ZooConstants.MaxShortNameLength"/>
  /// characters, or names that contain path separators, are stored as long-name (type 2)
  /// entries; a truncated DOS-safe short name is derived automatically.
  /// </param>
  /// <param name="data">The uncompressed file data.</param>
  /// <param name="method">
  /// The compression method.  Pass <see langword="null"/> to use the writer's default.
  /// If LZW produces output larger than the original, the entry is stored instead.
  /// </param>
  /// <param name="lastModified">The last-modification timestamp.  Defaults to the current UTC time.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="fileName"/> or <paramref name="data"/> is null.</exception>
  /// <exception cref="InvalidOperationException">Thrown when called after <see cref="Finish"/>.</exception>
  public void AddEntry(
    string fileName,
    byte[] data,
    ZooCompressionMethod? method = null,
    DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    var chosenMethod = method ?? this._defaultMethod;

    // --- Compute CRC-16 over uncompressed data ---
    ushort crc = Crc16.Compute(data);

    // --- Compress ---
    byte[] compressed = Compress(data, chosenMethod);
    // Fall back to Store if compression expanded the data.
    if (chosenMethod == ZooCompressionMethod.Lzw && compressed.Length >= data.Length) {
      compressed    = data;
      chosenMethod  = ZooCompressionMethod.Store;
    }

    // --- Derive short/long names ---
    string shortName = MakeShortName(fileName);
    string? longName = NeedsLongName(fileName, shortName) ? fileName : null;

    var entry = new ZooEntry {
      FileName          = shortName,
      LongFileName      = longName,
      CompressionMethod = chosenMethod,
      Crc16             = crc,
      OriginalSize      = (uint)data.Length,
      CompressedSize    = (uint)compressed.Length,
      LastModified      = lastModified ?? DateTime.UtcNow,
    };

    // Write the directory entry header and compressed data.
    // The nextOffset field will be patched in Finish() once we know
    // where the following entry starts (stored in entry.HeaderOffset).
    WriteDirectoryEntry(entry, compressed);

    this._pending.Add((entry, compressed));
  }

  /// <summary>
  /// Finalises the archive by patching all <c>nextOffset</c> chain pointers.
  /// Must be called (or the writer disposed) for a valid archive.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    if (this._pending.Count == 0) {
      // Empty archive: patch firstEntryOffset (at byte 24) to 0.
      long saved = this._stream.Position;
      this._stream.Position = 24;
      WriteUInt32LE(this._stream, 0u);
      WriteInt32LE(this._stream, 0);
      this._stream.Position = saved;
      this._stream.Flush();
      return;
    }

    // Patch each entry's nextOffset to point to the *next* entry's directory header.
    // The last entry gets nextOffset = 0 (end of archive).
    for (var i = 0; i < this._pending.Count; ++i) {
      var (entry, _) = this._pending[i];

      // The nextOffset field is at bytes 6–9 of the directory entry (after tag(4)+type(1)+method(1)).
      long fieldPos = entry.HeaderOffset + 4 + 1 + 1; // tag + type + method

      uint nextOffset = (i + 1 < this._pending.Count)
        ? (uint)this._pending[i + 1].Entry.HeaderOffset
        : 0u;

      long saved = this._stream.Position;
      this._stream.Position = fieldPos;
      WriteUInt32LE(this._stream, nextOffset);
      this._stream.Position = saved;
    }

    this._stream.Flush();
  }

  // ── Low-level serialisation ──────────────────────────────────────────────

  private void WriteArchiveHeader() {
    // 20 bytes: text description (ASCII, padded with nulls).
    byte[] text = new byte[20];
    byte[] textBytes = Encoding.ASCII.GetBytes(ZooConstants.DefaultHeaderText);
    int copyLen = Math.Min(textBytes.Length, 20);
    textBytes.AsSpan(0, copyLen).CopyTo(text);
    this._stream.Write(text);

    // Tag (4 bytes)
    WriteUInt32LE(this._stream, ZooConstants.Magic);

    // First entry offset (4 bytes) — will be patched in Finish(); write placeholder.
    long firstEntryOffsetPos = this._stream.Position;
    WriteUInt32LE(this._stream, 0u);          // placeholder

    // Minus-offset (4 bytes)
    WriteInt32LE(this._stream, 0);            // placeholder (0 is acceptable)

    // Major/minor version required to extract.
    this._stream.WriteByte(ZooConstants.MajorVersion);
    this._stream.WriteByte(ZooConstants.MinorVersion);

    // Archive header is exactly 34 bytes.
    // Patch the firstEntryOffset to point to byte 34 (the first entry follows immediately).
    long headerEnd = this._stream.Position; // should be 34
    this._stream.Position = firstEntryOffsetPos;
    WriteUInt32LE(this._stream, (uint)headerEnd);
    long negOffset = -(long)headerEnd;
    WriteInt32LE(this._stream, (int)negOffset);
    this._stream.Position = headerEnd;
  }

  private void WriteDirectoryEntry(ZooEntry entry, byte[] compressedData) {
    long headerStart = this._stream.Position;
    entry.HeaderOffset = headerStart;

    var (dosDate, dosTime) = ZooEntry.ToMsDosDateTime(entry.LastModified);

    byte type = (entry.LongFileName != null) ? ZooConstants.TypeLongName : ZooConstants.TypeFile;

    // Compute total header size so we can set dataOffset correctly.
    byte[] shortNameBytes = MakeShortNameBytes(entry.FileName);
    byte[] longNameBytes  = entry.LongFileName != null
      ? Encoding.Latin1.GetBytes(entry.LongFileName)
      : [];

    // dataOffset = headerStart + fixedSize(38) + shortName(13) + [2 + longNameLen]
    long dataOffset = headerStart
      + ZooConstants.DirectoryEntryFixedSize
      + 13
      + (type == ZooConstants.TypeLongName ? 2 + longNameBytes.Length : 0);

    entry.DataOffset = dataOffset;

    // Tag
    WriteUInt32LE(this._stream, ZooConstants.Magic);
    // Type
    this._stream.WriteByte(type);
    // Compression method
    this._stream.WriteByte((byte)entry.CompressionMethod);
    // nextOffset placeholder (patched in Finish)
    WriteUInt32LE(this._stream, 0u);
    // dataOffset
    WriteUInt32LE(this._stream, (uint)dataOffset);
    // Date / Time
    WriteUInt16LE(this._stream, dosDate);
    WriteUInt16LE(this._stream, dosTime);
    // CRC-16
    WriteUInt16LE(this._stream, entry.Crc16);
    // Original size
    WriteUInt32LE(this._stream, entry.OriginalSize);
    // Compressed size
    WriteUInt32LE(this._stream, entry.CompressedSize);
    // Versions
    this._stream.WriteByte(entry.MajorVersion);
    this._stream.WriteByte(entry.MinorVersion);
    // Deleted flag
    this._stream.WriteByte(entry.IsDeleted ? (byte)1 : (byte)0);
    // File structure (unused)
    this._stream.WriteByte(0);
    // Comment offset (0 = no comment)
    WriteUInt32LE(this._stream, 0u);
    // Comment length
    WriteUInt16LE(this._stream, 0);

    // Fixed part = 38 bytes total (verified: 4+1+1+4+4+2+2+2+4+4+1+1+1+1+4+2 = 38).

    // Short filename: exactly 13 bytes, null-terminated.
    this._stream.Write(shortNameBytes);

    // Long filename (type 2 only).
    if (type == ZooConstants.TypeLongName) {
      WriteUInt16LE(this._stream, (ushort)longNameBytes.Length);
      this._stream.Write(longNameBytes);
    }

    // Compressed data follows immediately.
    this._stream.Write(compressedData);
  }

  // ── Compression helpers ──────────────────────────────────────────────────

  private static byte[] Compress(byte[] data, ZooCompressionMethod method) {
    if (method == ZooCompressionMethod.Store)
      return data;

    using var ms = new MemoryStream();
    var encoder = new LzwEncoder(
      ms,
      minBits:      ZooConstants.LzwMinBits,
      maxBits:      ZooConstants.LzwMaxBits,
      useClearCode: true,
      useStopCode:  false,
      bitOrder:     BitOrder.LsbFirst);
    encoder.Encode(data);
    return ms.ToArray();
  }

  // ── Name helpers ─────────────────────────────────────────────────────────

  private static bool NeedsLongName(string original, string shortName) =>
    // Store as long name if the original differs from what we could fit in 12 chars.
    original != shortName || original.Length > ZooConstants.MaxShortNameLength;

  private static string MakeShortName(string name) {
    // Strip directory component.
    int slash = name.LastIndexOfAny(['/', '\\']);
    if (slash >= 0)
      name = name[(slash + 1)..];

    // Truncate to MaxShortNameLength.
    if (name.Length > ZooConstants.MaxShortNameLength)
      name = name[..ZooConstants.MaxShortNameLength];

    return name;
  }

  private static byte[] MakeShortNameBytes(string shortName) {
    // 13 bytes: up to 12 chars + null terminator.
    byte[] buf = new byte[13];
    byte[] src = Encoding.Latin1.GetBytes(shortName);
    int len = Math.Min(src.Length, 12);
    src.AsSpan(0, len).CopyTo(buf);
    // buf[len] = 0 already (array default).
    return buf;
  }

  // ── LE write helpers ─────────────────────────────────────────────────────

  private static void WriteUInt16LE(Stream s, ushort value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)(value >> 8));
  }

  private static void WriteUInt32LE(Stream s, uint value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)((value >> 8) & 0xFF));
    s.WriteByte((byte)((value >> 16) & 0xFF));
    s.WriteByte((byte)(value >> 24));
  }

  private static void WriteInt32LE(Stream s, int value) =>
    WriteUInt32LE(s, (uint)value);

  // ── IDisposable ──────────────────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Creates a Zoo archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries) {
    using var ms = new MemoryStream();
    using (var writer = new ZooWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
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
}
