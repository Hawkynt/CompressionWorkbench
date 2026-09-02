using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pfs0;

/// <summary>
/// Creates a Nintendo Switch PartitionFS (PFS0) archive.
/// </summary>
public sealed class Pfs0Writer : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="Pfs0Writer"/>.
  /// </summary>
  /// <param name="stream">The stream to write the PFS0 archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public Pfs0Writer(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds an entry to the archive.
  /// </summary>
  /// <param name="name">The entry name (UTF-8).</param>
  /// <param name="data">The raw entry data.</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    this._entries.Add((name, data));
  }

  /// <summary>
  /// Writes the PFS0 archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Switch SDK convention: alphabetically sort by name before serialization.
    this._entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

    // Pre-encode names so the string table is built in one pass and reused as the on-disk bytes.
    var encodedNames = new byte[this._entries.Count][];
    var nameOffsets = new uint[this._entries.Count];
    var stringTableSize = 0u;
    for (var i = 0; i < this._entries.Count; ++i) {
      encodedNames[i] = Encoding.UTF8.GetBytes(this._entries[i].Name);
      nameOffsets[i] = stringTableSize;
      // +1 for the NUL terminator that delimits the next name.
      stringTableSize += (uint)encodedNames[i].Length + 1;
    }

    // Header (16 bytes)
    Span<byte> header = stackalloc byte[Pfs0Constants.HeaderSize];
    Encoding.ASCII.GetBytes(Pfs0Constants.MagicPfs0String, header[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)this._entries.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], stringTableSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], 0); // reserved
    this._stream.Write(header);

    // Entries (24 bytes each, with offsets RELATIVE to the start of the data region).
    Span<byte> entryBuf = stackalloc byte[Pfs0Constants.EntrySize];
    var relativeOffset = 0UL;
    for (var i = 0; i < this._entries.Count; ++i) {
      var data = this._entries[i].Data;

      BinaryPrimitives.WriteUInt64LittleEndian(entryBuf[0..8], relativeOffset);
      BinaryPrimitives.WriteUInt64LittleEndian(entryBuf[8..16], (ulong)data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[16..20], nameOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[20..24], 0); // reserved

      this._stream.Write(entryBuf);
      relativeOffset += (ulong)data.Length;
    }

    // String table — null-terminated UTF-8 names back-to-back.
    for (var i = 0; i < this._entries.Count; ++i) {
      this._stream.Write(encodedNames[i]);
      this._stream.WriteByte(0);
    }

    // Data region — concatenated payloads in the same order as the entry table.
    for (var i = 0; i < this._entries.Count; ++i) {
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }
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
