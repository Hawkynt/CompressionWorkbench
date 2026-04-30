using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sarc;

/// <summary>
/// Creates a Nintendo SARC archive in little-endian format (Switch convention).
/// Entries are sorted by name hash on Finish() — the "sorted" in SARC — so that
/// the Switch SDK can binary-search by name without scanning the string table.
/// </summary>
public sealed class SarcWriter : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly uint _hashKey;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="SarcWriter"/>.
  /// </summary>
  /// <param name="stream">The destination stream (must be writable and seekable).</param>
  /// <param name="leaveOpen">If true, the underlying stream is left open on dispose.</param>
  /// <param name="hashKey">Polynomial multiplier; default 0x65 matches Nintendo's first-party tooling.</param>
  public SarcWriter(Stream stream, bool leaveOpen = false, uint hashKey = SarcConstants.DefaultHashKey) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._hashKey = hashKey;
  }

  /// <summary>Adds a file entry to the archive. Path separators should be forward slashes.</summary>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((name, data));
  }

  /// <summary>Finalizes the archive layout and writes it to the underlying stream.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Sort by hash ascending — required for Switch SDK lookup.
    var sorted = this._entries
      .Select(e => (e.Name, e.Data, Hash: SarcHash.Hash(e.Name, this._hashKey)))
      .OrderBy(e => e.Hash)
      .ToList();

    // Build SFNT string table. Each name is NUL-terminated UTF-8, padded to a
    // 4-byte boundary so that (offset/4) fits in the 24-bit attr field.
    using var sfnt = new MemoryStream();
    var nameOffsets = new int[sorted.Count];
    for (var i = 0; i < sorted.Count; ++i) {
      nameOffsets[i] = (int)sfnt.Position;
      var nameBytes = Encoding.UTF8.GetBytes(sorted[i].Name);
      sfnt.Write(nameBytes);
      sfnt.WriteByte(0);
      var padded = AlignUp((int)sfnt.Position, SarcConstants.NameAlignment);
      while (sfnt.Position < padded)
        sfnt.WriteByte(0);
    }
    var stringTable = sfnt.ToArray();

    // Compute layout. SARC header is 20 bytes (HeaderSize=0x14).
    var sfatOffset = SarcConstants.SarcHeaderBytes;
    var sfatEntriesOffset = sfatOffset + SarcConstants.SfatHeaderSize;
    var sfntHeaderOffset = sfatEntriesOffset + SarcConstants.SfatEntrySize * sorted.Count;
    var stringTableOffset = sfntHeaderOffset + SarcConstants.SfntHeaderSize;
    var dataRegionRaw = stringTableOffset + stringTable.Length;
    var dataOffset = AlignUp(dataRegionRaw, SarcConstants.DataAlignment);

    // Lay out file payloads consecutively from dataOffset (no per-entry alignment yet —
    // real SARC files often align each payload to its own natural alignment, but a
    // packed layout round-trips correctly and most tools don't require per-entry padding).
    var entryBegins = new uint[sorted.Count];
    var entryEnds = new uint[sorted.Count];
    var cursor = 0;
    for (var i = 0; i < sorted.Count; ++i) {
      entryBegins[i] = (uint)cursor;
      cursor += sorted[i].Data.Length;
      entryEnds[i] = (uint)cursor;
    }
    var totalFileSize = (long)dataOffset + cursor;
    if (totalFileSize > uint.MaxValue)
      throw new InvalidOperationException("SARC archive exceeds 4 GiB limit.");

    // SARC header (20 bytes, little-endian). Layout:
    //   magic(4) HeaderSize(2) BOM(2) FileSize(4) DataOffset(4) Version(2) Reserved(2)
    Span<byte> sarcHdr = stackalloc byte[SarcConstants.SarcHeaderBytes];
    "SARC"u8.CopyTo(sarcHdr[..4]);
    BinaryPrimitives.WriteUInt16LittleEndian(sarcHdr[4..6], SarcConstants.SarcHeaderSize);
    // BOM = 0xFEFF written little-endian → on-disk bytes FF FE. Reader's byte-pattern check
    // distinguishes this from the BE form (FE FF) without needing to know endianness yet.
    sarcHdr[6] = 0xFF;
    sarcHdr[7] = 0xFE;
    BinaryPrimitives.WriteUInt32LittleEndian(sarcHdr[8..12], (uint)totalFileSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sarcHdr[12..16], (uint)dataOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(sarcHdr[16..18], SarcConstants.DefaultVersion);
    BinaryPrimitives.WriteUInt16LittleEndian(sarcHdr[18..20], 0);
    this._stream.Write(sarcHdr);

    // SFAT header (12 bytes)
    Span<byte> sfatHdr = stackalloc byte[SarcConstants.SfatHeaderSize];
    "SFAT"u8.CopyTo(sfatHdr[..4]);
    BinaryPrimitives.WriteUInt16LittleEndian(sfatHdr[4..6], SarcConstants.SfatHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sfatHdr[6..8], (ushort)sorted.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(sfatHdr[8..12], this._hashKey);
    this._stream.Write(sfatHdr);

    // SFAT entries
    Span<byte> entryBuf = stackalloc byte[SarcConstants.SfatEntrySize];
    for (var i = 0; i < sorted.Count; ++i) {
      var nameWordOffset = (uint)(nameOffsets[i] / SarcConstants.NameAlignment);
      var attr = SarcConstants.NamePresentFlag | (nameWordOffset & 0x00FFFFFFu);

      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[0..4], sorted[i].Hash);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[4..8], attr);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[8..12], entryBegins[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[12..16], entryEnds[i]);
      this._stream.Write(entryBuf);
    }

    // SFNT header (8 bytes)
    Span<byte> sfntHdr = stackalloc byte[SarcConstants.SfntHeaderSize];
    "SFNT"u8.CopyTo(sfntHdr[..4]);
    BinaryPrimitives.WriteUInt16LittleEndian(sfntHdr[4..6], SarcConstants.SfntHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sfntHdr[6..8], 0);
    this._stream.Write(sfntHdr);

    // String table
    if (stringTable.Length > 0)
      this._stream.Write(stringTable);

    // Pad to dataOffset
    while (this._stream.Position < dataOffset)
      this._stream.WriteByte(0);

    // Data region
    foreach (var (_, data, _) in sorted)
      if (data.Length > 0)
        this._stream.Write(data);
  }

  private static int AlignUp(int value, int alignment)
    => (value + alignment - 1) & ~(alignment - 1);

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
