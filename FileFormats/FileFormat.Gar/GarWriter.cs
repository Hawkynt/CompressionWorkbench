using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Gar;

/// <summary>
/// Creates a Nintendo 3DS GAR v5 archive from in-memory file inputs.
/// </summary>
public sealed class GarWriter : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="GarWriter"/>.
  /// </summary>
  /// <param name="stream">The destination stream (must be writable).</param>
  /// <param name="leaveOpen">If true, the underlying stream is left open on dispose.</param>
  public GarWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file to the archive. The full filename (including extension) is stored;
  /// the writer groups files of the same extension into a single type entry on <see cref="Finish"/>.
  /// </summary>
  /// <param name="name">The full filename (e.g. "icon.bclim" or "rawfile" without extension).</param>
  /// <param name="data">The raw file payload.</param>
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

    // Split each filename into (baseName, extension) — the GAR file-type table groups files
    // by extension, so files of the same extension must share a single type entry.
    var split = this._entries.Select(e => SplitName(e.Name, e.Data)).ToList();

    // Build extension list, preserving first-seen order for stable output. Files inherit
    // their TypeIndex from the position of their extension in this list.
    var extOrder = new List<string>();
    var extToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var s in split) {
      if (extToIndex.ContainsKey(s.Ext))
        continue;

      extToIndex[s.Ext] = extOrder.Count;
      extOrder.Add(s.Ext);
    }

    var typeCount = extOrder.Count;
    var fileCount = split.Count;

    // Layout: header → type table → entry table → string pool → file data.
    var fileTypeOffset  = GarConstants.HeaderSize;
    var fileEntryOffset = fileTypeOffset + GarConstants.FileTypeEntrySize * typeCount;
    var stringPoolOffset = fileEntryOffset + GarConstants.FileEntrySize * fileCount;

    // Build the string pool: every unique extension followed by every per-file basename.
    // Reusing a single pool means filenames and extensions share NUL bytes when convenient,
    // but here we just append sequentially — simpler and avoids cross-checking offsets.
    using var pool = new MemoryStream();
    var extOffsets = new int[typeCount];
    for (var i = 0; i < typeCount; ++i) {
      extOffsets[i] = stringPoolOffset + (int)pool.Position;
      var bytes = Encoding.ASCII.GetBytes(extOrder[i]);
      pool.Write(bytes);
      pool.WriteByte(0);
    }

    var nameOffsets = new int[fileCount];
    for (var i = 0; i < fileCount; ++i) {
      nameOffsets[i] = stringPoolOffset + (int)pool.Position;
      var bytes = Encoding.ASCII.GetBytes(split[i].Base);
      pool.Write(bytes);
      pool.WriteByte(0);
    }

    var stringPool = pool.ToArray();
    var dataOffset = stringPoolOffset + stringPool.Length;

    // Compute per-entry data offsets (concatenated, no per-entry alignment).
    var entryDataOffsets = new int[fileCount];
    var cursor = dataOffset;
    for (var i = 0; i < fileCount; ++i) {
      entryDataOffsets[i] = cursor;
      cursor += split[i].Data.Length;
    }

    // Per-type index lists: GAR stores, for each type, a sequence of indices into the
    // entry table. Real Nintendo files put these in a separate region; placing them at
    // typeIndexOffset == fileEntryOffset is benign because readers ignore the field —
    // entries already carry their TypeIndex directly.
    var typeFileCounts = new int[typeCount];
    foreach (var s in split)
      typeFileCounts[extToIndex[s.Ext]]++;

    // Write header
    Span<byte> hdr = stackalloc byte[GarConstants.HeaderSize];
    GarConstants.MagicV5.CopyTo(hdr[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..8],   GarConstants.HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..12],  GarConstants.DefaultChunkCount);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..16], (uint)typeCount);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..20], (uint)fileCount);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..24], (uint)fileTypeOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..28], (uint)fileEntryOffset);
    this._stream.Write(hdr);

    // Write file-type table
    Span<byte> typeBuf = stackalloc byte[GarConstants.FileTypeEntrySize];
    for (var i = 0; i < typeCount; ++i) {
      typeBuf.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(typeBuf[0..4],   (uint)typeFileCounts[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(typeBuf[4..8],   (uint)fileEntryOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(typeBuf[8..12],  (uint)extOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(typeBuf[12..16], 0);
      this._stream.Write(typeBuf);
    }

    // Write file-entry table
    Span<byte> entryBuf = stackalloc byte[GarConstants.FileEntrySize];
    for (var i = 0; i < fileCount; ++i) {
      entryBuf.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[0..4],   (uint)split[i].Data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[4..8],   (uint)entryDataOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[8..12],  (uint)nameOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[12..16], (uint)extToIndex[split[i].Ext]);
      this._stream.Write(entryBuf);
    }

    // Write string pool
    if (stringPool.Length > 0)
      this._stream.Write(stringPool);

    // Write file data
    foreach (var s in split)
      if (s.Data.Length > 0)
        this._stream.Write(s.Data);
  }

  // The "extension" is everything after the last dot; files without a dot get an empty
  // extension which becomes a single empty-string entry in the type table. We strip the
  // leading dot so the on-disk extension string is bare ("bclim", not ".bclim").
  private static (string Base, string Ext, byte[] Data) SplitName(string name, byte[] data) {
    var dot = name.LastIndexOf('.');
    if (dot < 0)
      return (name, "", data);

    var baseName = name[..dot];
    var ext = name[(dot + 1)..];
    return (baseName, ext, data);
  }

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
