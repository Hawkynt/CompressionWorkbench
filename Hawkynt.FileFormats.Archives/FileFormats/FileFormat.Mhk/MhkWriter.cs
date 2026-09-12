using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mhk;

/// <summary>
/// Creates a Cyan Mohawk (MHK) archive.
/// </summary>
/// <remarks>
/// Layout written: outer "MHWK" frame, inner "RSRC" header, packed file payloads,
/// then a directory consisting of (file table, type table, per-type resource tables,
/// per-type name tables, name string pool). Sizes and offsets are back-patched once
/// the directory is laid out. All multi-byte integers are big-endian.
/// </remarks>
public sealed class MhkWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Type, ushort Id, string? Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="MhkWriter"/>.
  /// </summary>
  public MhkWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a resource to the archive. Type/id pairs identify resources; names are optional metadata.
  /// </summary>
  /// <param name="type">FourCC tag — must be exactly 4 ASCII bytes (e.g. "tBMP", "tWAV").</param>
  /// <param name="id">16-bit resource identifier.</param>
  /// <param name="name">Optional human-readable name; emitted into the per-type name table when non-null.</param>
  /// <param name="data">Raw resource payload.</param>
  public void AddEntry(string type, ushort id, string? name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(data);

    if (type.Length != MhkConstants.TypeTagSize)
      throw new ArgumentException($"Mohawk type tags must be exactly {MhkConstants.TypeTagSize} ASCII characters; got '{type}' ({type.Length} chars).", nameof(type));

    foreach (var ch in type) {
      if (ch > 0x7F)
        throw new ArgumentException($"Mohawk type tags must be ASCII: '{type}'.", nameof(type));
    }

    if (name is not null) {
      if (Encoding.ASCII.GetByteCount(name) > 255)
        throw new ArgumentException($"Mohawk names must be at most 255 bytes; got {Encoding.ASCII.GetByteCount(name)}.", nameof(name));
    }

    this._entries.Add((type, id, name, data));
  }

  /// <summary>
  /// Flushes the outer/inner headers, all payloads, and the resource directory to the underlying stream.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // 1. Reserve outer + RSRC headers; we'll back-patch their size/offset fields once we know them.
    var startPos = this._stream.Position;
    Span<byte> headerScratch = stackalloc byte[MhkConstants.OuterHeaderSize + MhkConstants.RsrcHeaderTotalSize];
    headerScratch.Clear();
    Encoding.ASCII.GetBytes(MhkConstants.OuterMagicString).CopyTo(headerScratch[..4]);
    Encoding.ASCII.GetBytes(MhkConstants.RsrcMagicString).CopyTo(headerScratch[MhkConstants.OuterHeaderSize..(MhkConstants.OuterHeaderSize + 4)]);
    this._stream.Write(headerScratch);

    // 2. Write each payload, recording its absolute offset and size.
    var fileEntries = new List<(uint Offset, uint Size)>(this._entries.Count);
    foreach (var (_, _, _, data) in this._entries) {
      var dataOffset = (uint)(this._stream.Position - startPos);
      if (data.Length > 0)
        this._stream.Write(data);
      fileEntries.Add((dataOffset, (uint)data.Length));
    }

    // 3. Group resources by type, preserving insertion order both across types and within each type.
    //    fileIndex is the 1-based slot in the global file table — equal to (entry-list index + 1)
    //    because we wrote payloads in that exact order.
    var typesInOrder = new List<string>();
    var typeBuckets = new Dictionary<string, List<(ushort Id, string? Name, ushort FileIndex)>>(StringComparer.Ordinal);
    for (var i = 0; i < this._entries.Count; ++i) {
      var (type, id, name, _) = this._entries[i];
      if (!typeBuckets.TryGetValue(type, out var bucket)) {
        bucket = [];
        typeBuckets[type] = bucket;
        typesInOrder.Add(type);
      }
      bucket.Add((id, name, (ushort)(i + 1)));
    }

    // 4. Build the directory in memory so we can compute the type-table offset before writing.
    //    Directory layout we emit:
    //      [0]                                                       file table
    //      [typeTableOffset]                                         type table
    //      [resTableOffsets[t]]                                      per-type resource tables
    //      [nameTableOffsets[t]]                                     per-type name tables (only when type has names)
    //      [poolBase]                                                packed null-terminated name strings
    using var directory = new MemoryStream();

    // 4a. File table at offset 0 of the directory.
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)fileEntries.Count);
    directory.Write(u32);

    Span<byte> fileEntryBuf = stackalloc byte[MhkConstants.FileTableEntrySize];
    foreach (var (offset, size) in fileEntries) {
      BinaryPrimitives.WriteUInt32BigEndian(fileEntryBuf[..4], offset);
      // 24-bit BE low part + 1-byte high part — historical Mohawk encoding for file size.
      fileEntryBuf[4] = (byte)((size >> 16) & 0xFF);
      fileEntryBuf[5] = (byte)((size >> 8) & 0xFF);
      fileEntryBuf[6] = (byte)(size & 0xFF);
      fileEntryBuf[7] = (byte)((size >> 24) & 0xFF);
      fileEntryBuf[8] = 0; // flags
      fileEntryBuf[9] = 0; // unknown high
      fileEntryBuf[10] = 0; // unknown low
      directory.Write(fileEntryBuf);
    }

    // 4b. Type table follows the file table.
    var typeTableOffset = directory.Position;
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)typesInOrder.Count);
    directory.Write(u16);

    // Reserve space for type-table entries; we'll patch resource/name table offsets after writing them.
    var typeTableEntryStart = directory.Position;
    var typeEntryScratch = new byte[MhkConstants.TypeTableEntrySize];
    for (var i = 0; i < typesInOrder.Count; ++i)
      directory.Write(typeEntryScratch);

    // 4c. Per-type resource tables.
    var resourceTableOffsets = new ushort[typesInOrder.Count];
    // Hoisted out of the loop to satisfy CA2014.
    Span<byte> resBuf = stackalloc byte[MhkConstants.ResourceTableEntrySize];
    for (var t = 0; t < typesInOrder.Count; ++t) {
      var bucket = typeBuckets[typesInOrder[t]];
      var offset = directory.Position;
      if (offset > ushort.MaxValue)
        throw new InvalidDataException("Mohawk directory exceeds 64 KB; resource table offset cannot fit in UInt16.");
      resourceTableOffsets[t] = (ushort)offset;

      BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)bucket.Count);
      directory.Write(u16);

      foreach (var (id, _, fileIndex) in bucket) {
        BinaryPrimitives.WriteUInt16BigEndian(resBuf[..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(resBuf[2..4], fileIndex);
        directory.Write(resBuf);
      }
    }

    // 4d. Per-type name tables (only emitted for types that have at least one named resource).
    //     The string pool begins immediately after the last name table; offsets are relative to
    //     the pool base, which is what the reader anchors against.
    var nameTableOffsets = new ushort[typesInOrder.Count];
    var pendingNamesPerType = new (List<(ushort Id, string Name)> Names, long NameTableStart)[typesInOrder.Count];
    for (var t = 0; t < typesInOrder.Count; ++t) {
      var bucket = typeBuckets[typesInOrder[t]];
      var named = bucket.Where(r => r.Name is not null).Select(r => (r.Id, Name: r.Name!)).ToList();

      if (named.Count == 0) {
        nameTableOffsets[t] = 0;
        pendingNamesPerType[t] = (new List<(ushort, string)>(), -1);
        continue;
      }

      var offset = directory.Position;
      if (offset > ushort.MaxValue)
        throw new InvalidDataException("Mohawk directory exceeds 64 KB; name table offset cannot fit in UInt16.");
      nameTableOffsets[t] = (ushort)offset;

      BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)named.Count);
      directory.Write(u16);

      // Reserve name-table slots; back-patch string offsets once the pool is laid out.
      var slotsStart = directory.Position;
      var emptySlot = new byte[MhkConstants.NameTableEntrySize];
      for (var i = 0; i < named.Count; ++i)
        directory.Write(emptySlot);

      pendingNamesPerType[t] = (named, slotsStart);
    }

    // 4e. String pool: emit packed null-terminated ASCII names, recording each name's pool offset
    //     so the per-type name tables can point at them.
    var poolBase = directory.Position;
    var nameOffsets = new Dictionary<string, ushort>(StringComparer.Ordinal);
    for (var t = 0; t < typesInOrder.Count; ++t) {
      var (named, _) = pendingNamesPerType[t];
      foreach (var (_, n) in named) {
        if (nameOffsets.ContainsKey(n))
          continue;
        var poolOffset = directory.Position - poolBase;
        if (poolOffset > ushort.MaxValue)
          throw new InvalidDataException("Mohawk name string pool exceeds 64 KB; cannot represent in UInt16 offsets.");
        nameOffsets[n] = (ushort)poolOffset;
        directory.Write(Encoding.ASCII.GetBytes(n));
        directory.WriteByte(0);
      }
    }

    // 4f. Back-patch name-table slots now that we know each string's pool offset.
    Span<byte> slotBuf = stackalloc byte[MhkConstants.NameTableEntrySize];
    for (var t = 0; t < typesInOrder.Count; ++t) {
      var (named, slotsStart) = pendingNamesPerType[t];
      if (named.Count == 0)
        continue;

      var savedPos = directory.Position;
      directory.Position = slotsStart;
      foreach (var (nameId, nameStr) in named) {
        BinaryPrimitives.WriteUInt16BigEndian(slotBuf[..2], nameOffsets[nameStr]);
        BinaryPrimitives.WriteUInt16BigEndian(slotBuf[2..4], nameId);
        directory.Write(slotBuf);
      }
      directory.Position = savedPos;
    }

    // 4g. Back-patch type-table entries now that resource/name table offsets are known.
    {
      var savedPos = directory.Position;
      directory.Position = typeTableEntryStart;
      Span<byte> entryBuf = stackalloc byte[MhkConstants.TypeTableEntrySize];
      for (var t = 0; t < typesInOrder.Count; ++t) {
        Encoding.ASCII.GetBytes(typesInOrder[t]).CopyTo(entryBuf[..4]);
        BinaryPrimitives.WriteUInt16BigEndian(entryBuf[4..6], resourceTableOffsets[t]);
        BinaryPrimitives.WriteUInt16BigEndian(entryBuf[6..8], nameTableOffsets[t]);
        directory.Write(entryBuf);
      }
      directory.Position = savedPos;
    }

    // 5. Write the directory to the output stream and capture its absolute offset.
    var directoryAbsoluteOffset = (uint)(this._stream.Position - startPos);
    directory.Position = 0;
    directory.CopyTo(this._stream);

    var totalFileSize = (uint)(this._stream.Position - startPos);
    var outerBodySize = totalFileSize - MhkConstants.OuterHeaderSize;
    var rsrcBodySize  = totalFileSize - (MhkConstants.OuterHeaderSize + 8);

    if (typeTableOffset > ushort.MaxValue)
      throw new InvalidDataException("Mohawk type-table offset exceeds 64 KB.");

    // 6. Back-patch headers.
    var savedEnd = this._stream.Position;
    this._stream.Position = startPos;

    Span<byte> headerOut = stackalloc byte[MhkConstants.OuterHeaderSize + MhkConstants.RsrcHeaderTotalSize];
    headerOut.Clear();
    Encoding.ASCII.GetBytes(MhkConstants.OuterMagicString).CopyTo(headerOut[..4]);
    BinaryPrimitives.WriteUInt32BigEndian(headerOut[4..8], outerBodySize);

    Encoding.ASCII.GetBytes(MhkConstants.RsrcMagicString).CopyTo(headerOut[8..12]);
    BinaryPrimitives.WriteUInt32BigEndian(headerOut[12..16], rsrcBodySize);
    BinaryPrimitives.WriteUInt16BigEndian(headerOut[16..18], MhkConstants.DefaultVersion);
    BinaryPrimitives.WriteUInt16BigEndian(headerOut[18..20], MhkConstants.DefaultCompaction);
    BinaryPrimitives.WriteUInt32BigEndian(headerOut[20..24], totalFileSize);
    BinaryPrimitives.WriteUInt32BigEndian(headerOut[24..28], directoryAbsoluteOffset);
    BinaryPrimitives.WriteUInt16BigEndian(headerOut[28..30], (ushort)typeTableOffset);
    BinaryPrimitives.WriteUInt16BigEndian(headerOut[30..32], 0); // reserved
    this._stream.Write(headerOut);

    this._stream.Position = savedEnd;
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
