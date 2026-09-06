using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pak;

/// <summary>
/// Writes canonical Quake PACK archives: payloads first, one trailing directory,
/// then patches the 12-byte header with the directory offset and length.
/// Entries are stored verbatim; Quake PAK defines no per-entry compression.
/// </summary>
public sealed class PakWriter : IDisposable {
  private readonly Stream _stream;
  private readonly List<(string Name, byte[] NameBytes, int Offset, int Length)> _entries = [];
  private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
  private bool _finished;

  /// <summary>Creates a new PACK archive on a seekable writable stream.</summary>
  public PakWriter(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite || !stream.CanSeek)
      throw new NotSupportedException("Quake PAK writing requires a seekable, writable stream.");
    this._stream = stream;
    stream.Position = 0;
    stream.SetLength(0);
    Span<byte> header = stackalloc byte[PakReader.HeaderSize];
    header.Clear();
    "PACK"u8.CopyTo(header);
    stream.Write(header);
  }

  /// <summary>Adds one stored file payload.</summary>
  public void AddEntry(string fileName, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("The PAK directory has already been written.");
    if (this._entries.Count >= PakReader.MaxEntries)
      throw new NotSupportedException($"Quake PAK supports at most {PakReader.MaxEntries} directory entries.");
    ArgumentNullException.ThrowIfNull(data);
    var nameBytes = EncodeName(fileName);
    var terminator = Array.IndexOf(nameBytes, (byte)0);
    var normalized = Encoding.ASCII.GetString(nameBytes, 0, terminator >= 0 ? terminator : nameBytes.Length);
    if (!this._names.Add(normalized))
      throw new ArgumentException($"Duplicate Quake PAK entry '{normalized}'.", nameof(fileName));
    if (this._stream.Position > int.MaxValue || data.LongLength > int.MaxValue || this._stream.Position + data.LongLength > int.MaxValue)
      throw new NotSupportedException("Quake PAK uses signed 32-bit file offsets and lengths.");

    var offset = checked((int)this._stream.Position);
    this._stream.Write(data);
    this._entries.Add((normalized, nameBytes, offset, data.Length));
  }

  /// <summary>Writes the trailing directory and patches the PACK header.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    if (this._stream.Position > int.MaxValue)
      throw new NotSupportedException("Quake PAK directory offset exceeds the signed 32-bit format field.");
    var directoryOffset = checked((int)this._stream.Position);
    var directoryLength = checked(this._entries.Count * PakReader.DirectoryEntrySize);
    if ((long)directoryOffset + directoryLength > int.MaxValue)
      throw new NotSupportedException("Quake PAK exceeds the signed 32-bit format limit.");

    Span<byte> record = stackalloc byte[PakReader.DirectoryEntrySize];
    foreach (var entry in this._entries) {
      record.Clear();
      entry.NameBytes.CopyTo(record);
      BinaryPrimitives.WriteInt32LittleEndian(record[56..60], entry.Offset);
      BinaryPrimitives.WriteInt32LittleEndian(record[60..64], entry.Length);
      this._stream.Write(record);
    }

    Span<byte> directoryFields = stackalloc byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(directoryFields[..4], directoryOffset);
    BinaryPrimitives.WriteInt32LittleEndian(directoryFields[4..], directoryLength);
    this._stream.Position = 4;
    this._stream.Write(directoryFields);
    this._stream.SetLength((long)directoryOffset + directoryLength);
    this._stream.Flush();
  }

  internal static byte[] EncodeName(string fileName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
    var normalized = fileName.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("Quake PAK entry name must identify a file.", nameof(fileName));
    foreach (var part in normalized.Split('/')) {
      if (part.Length == 0 || part is "." or "..")
        throw new ArgumentException("Unsafe Quake PAK entry path.", nameof(fileName));
    }
    if (normalized.Any(c => c == '\0' || c > '\x7F'))
      throw new ArgumentException("Quake PAK names are 7-bit archive paths.", nameof(fileName));
    var bytes = Encoding.ASCII.GetBytes(normalized);
    if (bytes.Length >= PakReader.NameFieldSize)
      throw new ArgumentException("Quake PAK entry names are limited to 55 bytes plus NUL.", nameof(fileName));
    var field = new byte[PakReader.NameFieldSize];
    bytes.CopyTo(field, 0);
    return field;
  }

  /// <inheritdoc />
  public void Dispose() { }
}
