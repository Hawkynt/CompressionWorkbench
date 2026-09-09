using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pak;

/// <summary>
/// Reads id Software Quake PACK archives: a 12-byte <c>PACK</c> header followed
/// by stored file payloads and a 64-byte-per-entry directory referenced by the
/// header. The directory may be physically anywhere in a readable archive; the
/// in-place modifier deliberately requires the canonical trailing-directory form.
/// </summary>
public sealed class PakReader : IDisposable {
  /// <summary>Size of the fixed PACK header in bytes.</summary>
  public const int HeaderSize = 12;
  /// <summary>Size of one PACK directory record in bytes.</summary>
  public const int DirectoryEntrySize = 64;
  /// <summary>Size of the NUL-padded file-name field in one directory record.</summary>
  public const int NameFieldSize = 56;
  /// <summary>Maximum directory entries accepted by the original Quake engine.</summary>
  public const int MaxEntries = 2048;

  private readonly Stream _stream;
  private readonly List<PakEntry> _entries = [];
  private int _nextIndex;
  private PakEntry? _current;

  /// <summary>Gets the byte offset of the PACK directory.</summary>
  public int DirectoryOffset { get; }

  /// <summary>Gets the directory length in bytes.</summary>
  public int DirectoryLength { get; }

  /// <summary>Gets all directory entries in on-disk order.</summary>
  public IReadOnlyList<PakEntry> Entries => this._entries;

  /// <summary>Reads a PAK archive from a seekable stream.</summary>
  public PakReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new NotSupportedException("Quake PAK reading requires a seekable, readable stream.");
    this._stream = stream;

    if (stream.Length < HeaderSize)
      throw new InvalidDataException("Quake PAK is shorter than its 12-byte PACK header.");

    Span<byte> header = stackalloc byte[HeaderSize];
    stream.Position = 0;
    stream.ReadExactly(header);
    if (!header[..4].SequenceEqual("PACK"u8))
      throw new InvalidDataException("Not a Quake PAK archive: missing PACK magic.");

    var directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
    var directoryLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
    if (directoryOffset < HeaderSize || directoryLength < 0 || directoryLength % DirectoryEntrySize != 0)
      throw new InvalidDataException("Quake PAK has an invalid directory offset or length.");
    if ((long)directoryOffset + directoryLength > stream.Length)
      throw new InvalidDataException("Quake PAK directory extends beyond end of stream.");

    var count = directoryLength / DirectoryEntrySize;
    if (count > MaxEntries)
      throw new NotSupportedException($"Quake PAK contains {count} entries; the original engine limit is {MaxEntries}.");

    this.DirectoryOffset = directoryOffset;
    this.DirectoryLength = directoryLength;

    var record = new byte[DirectoryEntrySize];
    stream.Position = directoryOffset;
    for (var i = 0; i < count; ++i) {
      stream.ReadExactly(record);
      var terminator = Array.IndexOf(record, (byte)0, 0, NameFieldSize);
      var nameLength = terminator >= 0 ? terminator : NameFieldSize;
      var name = Encoding.ASCII.GetString(record, 0, nameLength);
      var fileOffset = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(56, 4));
      var fileLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(60, 4));
      if (fileOffset < 0 || fileLength < 0 || (long)fileOffset + fileLength > stream.Length)
        throw new InvalidDataException($"Quake PAK entry '{name}' points outside the archive.");
      this._entries.Add(new PakEntry { FileName = name, FileOffset = fileOffset, Size = fileLength });
    }
  }

  /// <summary>Gets the next directory entry, or null after the last one.</summary>
  public PakEntry? GetNextEntry() {
    if (this._nextIndex >= this._entries.Count) {
      this._current = null;
      return null;
    }
    this._current = this._entries[this._nextIndex++];
    return this._current;
  }

  /// <summary>Reads the stored bytes of the current entry.</summary>
  public byte[] ReadEntryData() {
    var entry = this._current ?? throw new InvalidOperationException("Call GetNextEntry() before ReadEntryData().");
    var result = new byte[entry.Size];
    this._stream.Position = entry.FileOffset;
    this._stream.ReadExactly(result);
    return result;
  }

  /// <inheritdoc />
  public void Dispose() { }
}
