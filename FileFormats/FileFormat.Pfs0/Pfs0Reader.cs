using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pfs0;

/// <summary>
/// Reads entries from a Nintendo Switch PartitionFS (PFS0) archive.
/// </summary>
public sealed class Pfs0Reader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries in the PFS0 archive.</summary>
  public IReadOnlyList<Pfs0Entry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="Pfs0Reader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the PFS0 archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public Pfs0Reader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < Pfs0Constants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid PFS0 archive.");

    Span<byte> header = stackalloc byte[Pfs0Constants.HeaderSize];
    ReadExact(header);

    var magic = Encoding.ASCII.GetString(header[..4]);

    // HFS0 has the same outer shape but 64-byte entries with hashes — refuse cleanly so the user knows why.
    if (magic == Pfs0Constants.MagicHfs0String)
      throw new NotSupportedException("HFS0 is not yet supported, use PFS0.");

    if (magic != Pfs0Constants.MagicPfs0String)
      throw new InvalidDataException($"Invalid PFS0 magic: {magic}");

    var numFiles = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    var stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    // header[12..16] reserved — ignored on read

    if (numFiles > int.MaxValue)
      throw new InvalidDataException($"Invalid file count: {numFiles}");

    var fileCount = (int)numFiles;
    var entriesSize = (long)fileCount * Pfs0Constants.EntrySize;
    var dataRegionStart = Pfs0Constants.HeaderSize + entriesSize + stringTableSize;

    if (dataRegionStart > stream.Length)
      throw new InvalidDataException("PFS0 header declares a data region beyond the end of the stream.");

    // Read raw 24-byte entries first, then string table, then resolve names.
    var rawEntries = new (ulong DataOffset, ulong DataSize, uint NameOffset)[fileCount];
    Span<byte> entryBuf = stackalloc byte[Pfs0Constants.EntrySize];
    for (var i = 0; i < fileCount; ++i) {
      ReadExact(entryBuf);
      rawEntries[i] = (
        BinaryPrimitives.ReadUInt64LittleEndian(entryBuf[0..8]),
        BinaryPrimitives.ReadUInt64LittleEndian(entryBuf[8..16]),
        BinaryPrimitives.ReadUInt32LittleEndian(entryBuf[16..20])
        // entryBuf[20..24] reserved
      );
    }

    var stringTable = new byte[stringTableSize];
    if (stringTableSize > 0)
      ReadExact(stringTable);

    var entries = new List<Pfs0Entry>(fileCount);
    for (var i = 0; i < fileCount; ++i) {
      var (relOffset, size, nameOffset) = rawEntries[i];
      if (nameOffset > stringTable.Length)
        throw new InvalidDataException($"PFS0 entry #{i} name offset {nameOffset} is outside the string table.");

      var name = ReadCStringUtf8(stringTable, (int)nameOffset);

      // Translate the on-disk relative offset (relative to data region) to an absolute stream offset for the caller.
      var absoluteOffset = dataRegionStart + (long)relOffset;
      if (absoluteOffset < 0 || absoluteOffset + (long)size > stream.Length)
        throw new InvalidDataException($"PFS0 entry '{name}' data range exceeds the stream length.");

      entries.Add(new Pfs0Entry {
        Name = name,
        Offset = absoluteOffset,
        Size = (long)size,
      });
    }

    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw data for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The raw entry data.</returns>
  public byte[] Extract(Pfs0Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"PFS0 entry '{entry.Name}' is too large to extract into a single byte array.");

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private static string ReadCStringUtf8(ReadOnlySpan<byte> table, int offset) {
    var slice = table[offset..];
    var nul = slice.IndexOf((byte)0);
    if (nul < 0)
      nul = slice.Length;
    return Encoding.UTF8.GetString(slice[..nul]);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of PFS0 stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
