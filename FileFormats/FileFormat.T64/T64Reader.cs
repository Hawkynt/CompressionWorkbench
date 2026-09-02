#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.T64;

/// <summary>
/// Reads a Commodore 64 T64 tape container and exposes the program entries recorded in its directory.
/// </summary>
public sealed class T64Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<T64Entry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<T64Entry> Entries => _entries;
  /// <summary>
  /// Gets or sets the tape name.
  /// </summary>
public string TapeName { get; private set; } = "";

  private const int HeaderSize = 64;
  private const int EntrySize = 32;

  /// <summary>
  /// Initializes a new instance of <see cref="T64Reader"/>.
  /// </summary>
public T64Reader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("T64: file too small.");

    // Validate magic
    var sig = Encoding.ASCII.GetString(_data, 0, 3);
    if (sig != "C64")
      throw new InvalidDataException("T64: invalid magic.");

    var maxEntries = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(34));
    var usedEntries = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(36));

    // Tape name (24 bytes at offset 40)
    TapeName = Encoding.ASCII.GetString(_data, 40, 24).TrimEnd('\0', ' ');

    // Read directory entries
    for (var i = 0; i < maxEntries; i++) {
      var off = HeaderSize + i * EntrySize;
      if (off + EntrySize > _data.Length) break;

      var entryType = _data[off];
      if (entryType == 0) continue; // free slot

      var startAddr = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 2));
      var endAddr = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 4));
      var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 8));

      // Filename: 16 bytes at offset 16 within entry
      var nameBytes = _data.AsSpan(off + 16, 16);
      var nameEnd = 16;
      for (var j = 15; j >= 0; j--) {
        if (nameBytes[j] != 0x20 && nameBytes[j] != 0x00) { nameEnd = j + 1; break; }
        if (j == 0) nameEnd = 0;
      }
      var name = Encoding.ASCII.GetString(_data, off + 16, nameEnd);

      var size = endAddr > startAddr ? endAddr - startAddr : 0;

      _entries.Add(new T64Entry {
        Name = name,
        Size = size,
        EntryType = entryType,
        StartAddress = startAddr,
        EndAddress = endAddr,
        DataOffset = dataOffset,
      });
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(T64Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];

    var offset = entry.DataOffset;
    var length = (int)entry.Size;
    if (offset + length > _data.Length)
      length = Math.Max(0, _data.Length - offset);
    return _data.AsSpan(offset, length).ToArray();
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
