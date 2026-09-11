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
  /// Gets the T64 format version.
  /// </summary>
  public ushort Version { get; private set; }
  /// <summary>
  /// Gets the number of directory slots reserved by the image.
  /// </summary>
  public ushort DirectoryEntryCount { get; private set; }
  /// <summary>
  /// Gets the used-entry count stored in the header.
  /// </summary>
  public ushort UsedEntryCount { get; private set; }
  /// <summary>
  /// Gets or sets the tape name.
  /// </summary>
  public string TapeName { get; private set; } = "";

  private const int HeaderSize = 64;
  private const int EntrySize = 32;
  private const ushort Conv64BrokenEndAddress = 0xC3C6;

  /// <summary>
  /// Initializes a new instance of <see cref="T64Reader"/>.
  /// </summary>
  public T64Reader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("T64: stream must be readable.", nameof(stream));
    if (stream.CanSeek) {
      if (stream.Length > int.MaxValue)
        throw new InvalidDataException("T64: images larger than 2 GiB are not supported by the in-memory reader.");
      stream.Position = 0;
    }

    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("T64: file too small.");

    var signature = Encoding.ASCII.GetString(_data, 0, 32).TrimEnd('\0', ' ');
    if (!IsKnownSignature(signature))
      throw new InvalidDataException("T64: invalid magic.");

    Version = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(32));
    DirectoryEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(34));
    UsedEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(36));

    var directoryEnd = checked(HeaderSize + DirectoryEntryCount * EntrySize);
    if (directoryEnd > _data.Length)
      throw new InvalidDataException("T64: directory extends beyond end of image.");

    // Tape name (24 bytes at offset 40)
    TapeName = Encoding.ASCII.GetString(_data, 40, 24).TrimEnd('\0', ' ');

    var rawEntries = new List<RawEntry>();
    for (var i = 0; i < DirectoryEntryCount; i++) {
      var off = HeaderSize + i * EntrySize;
      var entryType = _data[off];
      if (entryType == 0) continue;

      var fileType = _data[off + 1];
      var startAddr = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 2));
      var endAddr = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 4));
      var rawDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 8));
      if (rawDataOffset > int.MaxValue)
        throw new InvalidDataException($"T64: entry {i} data offset is outside the supported image range.");
      var dataOffset = (int)rawDataOffset;
      if (dataOffset < directoryEnd || dataOffset > _data.Length)
        throw new InvalidDataException($"T64: entry {i} data offset is outside the payload region.");

      var nameBytes = _data.AsSpan(off + 16, 16);
      var nameLength = 16;
      while (nameLength > 0 && nameBytes[nameLength - 1] is 0x00 or 0x20)
        --nameLength;
      var name = Encoding.ASCII.GetString(nameBytes[..nameLength]);

      rawEntries.Add(new RawEntry(i, entryType, fileType, startAddr, endAddr, dataOffset, name));
    }

    var ordered = rawEntries.OrderBy(static e => e.DataOffset).ToArray();
    for (var i = 0; i < ordered.Length; i++) {
      var raw = ordered[i];
      var isLastPayload = i + 1 >= ordered.Length;
      var nextOffset = isLastPayload ? _data.Length : ordered[i + 1].DataOffset;
      if (nextOffset < raw.DataOffset)
        throw new InvalidDataException($"T64: entry {raw.DirectoryIndex} has an invalid payload order.");

      var physicalBytesAvailable = nextOffset - raw.DataOffset;
      var size = ResolveSize(raw, physicalBytesAvailable);
      if (size < 0)
        throw new InvalidDataException($"T64: entry {raw.DirectoryIndex} has a negative payload length.");

      if (size > physicalBytesAvailable) {
        // Tape writers routinely overstate the last record's end address by a byte or two, so the
        // declared length runs past EOF on images that are otherwise intact. VICE and t64fix read
        // those tapes by stopping at the end of the file, and this mirrors that. An overrun into a
        // *following* payload is genuine corruption and still fails.
        if (!isLastPayload)
          throw new InvalidDataException($"T64: entry {raw.DirectoryIndex} extends into the next payload.");

        size = physicalBytesAvailable;
      }

      _entries.Add(new T64Entry {
        DirectoryIndex = raw.DirectoryIndex,
        Name = raw.Name,
        Size = size,
        EntryType = raw.EntryType,
        FileType = raw.FileType,
        StartAddress = raw.StartAddress,
        EndAddress = raw.EndAddress,
        DataOffset = raw.DataOffset,
      });
    }

    _entries.Sort(static (a, b) => a.DirectoryIndex.CompareTo(b.DirectoryIndex));
  }

  private static long ResolveSize(RawEntry entry, int physicalBytesAvailable) {
    // CONV64 historically wrote $C3C6 into every end-address field. VICE and
    // t64fix recover such records from the next payload offset (or EOF for the
    // final entry), so mirror that public behaviour without depending on their
    // implementation code.
    if (entry.EntryType == 1 && entry.EndAddress == Conv64BrokenEndAddress)
      return physicalBytesAvailable;

    // T64 stores a 16-bit exclusive end address. An entry that ends exactly at
    // $10000 therefore wraps the stored field to zero; this is the only valid
    // wrap and is needed for payloads reaching the top of C64 memory.
    var logicalEnd = entry.EndAddress == 0 && entry.StartAddress != 0
      ? 0x10000L
      : entry.EndAddress;
    if (logicalEnd < entry.StartAddress)
      throw new InvalidDataException($"T64: entry {entry.DirectoryIndex} has an end address before its start address.");

    return logicalEnd - entry.StartAddress;
  }

  private static bool IsKnownSignature(string signature)
    => signature.StartsWith("C64S tape image file", StringComparison.Ordinal)
       || signature.StartsWith("C64 tape image file", StringComparison.Ordinal)
       || signature.StartsWith("C64S tape file", StringComparison.Ordinal)
       || signature.StartsWith("C64S image file", StringComparison.Ordinal);

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(T64Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];
    if (entry.DataOffset < 0 || entry.Size > int.MaxValue || (long)entry.DataOffset + entry.Size > _data.Length)
      throw new InvalidDataException("T64: entry payload is outside the image.");

    return _data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }

  private readonly record struct RawEntry(
    int DirectoryIndex,
    byte EntryType,
    byte FileType,
    ushort StartAddress,
    ushort EndAddress,
    int DataOffset,
    string Name);
}
