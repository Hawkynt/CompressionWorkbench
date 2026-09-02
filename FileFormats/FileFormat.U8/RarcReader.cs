#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rarc;

/// <summary>
/// Reads a Nintendo RARC resource archive, walking its directory nodes to enumerate the files it holds.
/// </summary>
public sealed class RarcReader {
  private readonly Stream _stream;
  private readonly long _baseOffset;
  private readonly long _archiveEnd;
  private readonly long _fileDataOffset;
  private readonly long _fileDataEnd;
  private readonly byte[] _stringTable;
  private readonly List<NodeRecord> _nodes;
  private readonly List<FileRecord> _fileRecords;
  private readonly List<RarcEntry> _entries = [];

  /// <summary>
  /// Initializes a new instance of <see cref="RarcReader"/>.
  /// </summary>
public RarcReader(Stream stream) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanRead)
      throw new ArgumentException("Stream must be readable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));

    this._baseOffset = stream.Position;
    if (stream.Length - this._baseOffset < RarcConstants.HeaderSize)
      throw new InvalidDataException("RARC stream is shorter than its fixed header.");

    Span<byte> header = stackalloc byte[RarcConstants.HeaderSize];
    stream.ReadExactly(header);
    if (!header[..4].SequenceEqual("RARC"u8))
      throw new InvalidDataException("Not a Nintendo RARC archive.");

    var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
    if (declaredSize < RarcConstants.HeaderSize + RarcConstants.DataHeaderSize)
      throw new InvalidDataException("RARC declared size is too small to contain both headers.");
    if (declaredSize > stream.Length - this._baseOffset)
      throw new InvalidDataException("RARC declared size extends beyond the input stream.");
    this._archiveEnd = checked(this._baseOffset + declaredSize);

    var dataHeaderRelative = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
    if (dataHeaderRelative < RarcConstants.HeaderSize)
      throw new InvalidDataException("RARC data header offset overlaps the file header.");
    var dataHeaderOffset = checked(this._baseOffset + dataHeaderRelative);
    ValidateRange(dataHeaderOffset, RarcConstants.DataHeaderSize, "RARC data header");

    var fileDataRelative = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
    var totalFileDataSize = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
    this._fileDataOffset = checked(dataHeaderOffset + fileDataRelative);
    this._fileDataEnd = checked(this._fileDataOffset + totalFileDataSize);
    ValidateRange(this._fileDataOffset, totalFileDataSize, "RARC file-data section");

    Span<byte> dataHeader = stackalloc byte[RarcConstants.DataHeaderSize];
    ReadAt(dataHeaderOffset, dataHeader, "RARC data header");

    var nodeCount = BinaryPrimitives.ReadUInt32BigEndian(dataHeader[0..4]);
    var nodeRelative = BinaryPrimitives.ReadUInt32BigEndian(dataHeader[4..8]);
    var fileEntryCount = BinaryPrimitives.ReadUInt32BigEndian(dataHeader[8..12]);
    var fileEntryRelative = BinaryPrimitives.ReadUInt32BigEndian(dataHeader[12..16]);
    var stringLength = BinaryPrimitives.ReadUInt32BigEndian(dataHeader[16..20]);
    var stringRelative = BinaryPrimitives.ReadUInt32BigEndian(dataHeader[20..24]);

    if (nodeCount == 0)
      throw new InvalidDataException("RARC must contain a root directory node.");
    if (nodeCount > int.MaxValue || fileEntryCount > int.MaxValue || stringLength > int.MaxValue)
      throw new InvalidDataException("RARC metadata is too large for the in-memory reader.");

    var nodeOffset = checked(dataHeaderOffset + nodeRelative);
    var fileEntryOffset = checked(dataHeaderOffset + fileEntryRelative);
    var stringOffset = checked(dataHeaderOffset + stringRelative);
    ValidateRange(nodeOffset, checked((long)nodeCount * RarcConstants.NodeSize), "RARC directory-node table");
    ValidateRange(fileEntryOffset, checked((long)fileEntryCount * RarcConstants.FileEntrySize), "RARC file-entry table");
    ValidateRange(stringOffset, stringLength, "RARC string table");

    this._stringTable = new byte[(int)stringLength];
    if (this._stringTable.Length > 0)
      ReadAt(stringOffset, this._stringTable, "RARC string table");

    this._nodes = new List<NodeRecord>((int)nodeCount);
    var nodeBuffer = new byte[RarcConstants.NodeSize];
    for (var i = 0; i < (int)nodeCount; ++i) {
      ReadAt(checked(nodeOffset + (long)i * RarcConstants.NodeSize), nodeBuffer, "RARC directory node");
      var nameOffset = BinaryPrimitives.ReadUInt32BigEndian(nodeBuffer.AsSpan(4, 4));
      var entryCount = BinaryPrimitives.ReadUInt16BigEndian(nodeBuffer.AsSpan(10, 2));
      var firstEntry = BinaryPrimitives.ReadUInt32BigEndian(nodeBuffer.AsSpan(12, 4));
      if ((ulong)firstEntry + entryCount > fileEntryCount)
        throw new InvalidDataException($"RARC node {i} references file entries outside the file-entry table.");
      this._nodes.Add(new NodeRecord(
        ReadName(nameOffset),
        BinaryPrimitives.ReadUInt16BigEndian(nodeBuffer.AsSpan(8, 2)),
        entryCount,
        firstEntry));
    }

    this._fileRecords = new List<FileRecord>((int)fileEntryCount);
    var entryBuffer = new byte[RarcConstants.FileEntrySize];
    for (var i = 0; i < (int)fileEntryCount; ++i) {
      ReadAt(checked(fileEntryOffset + (long)i * RarcConstants.FileEntrySize), entryBuffer, "RARC file entry");
      var typeAndName = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(4, 4));
      var attributes = (RarcEntryAttributes)(typeAndName >> 24);
      var nameOffset = typeAndName & 0x00FF_FFFFu;
      this._fileRecords.Add(new FileRecord(
        BinaryPrimitives.ReadUInt16BigEndian(entryBuffer.AsSpan(0, 2)),
        BinaryPrimitives.ReadUInt16BigEndian(entryBuffer.AsSpan(2, 2)),
        attributes,
        ReadName(nameOffset),
        BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(8, 4)),
        BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(12, 4))));
    }

    var stack = new HashSet<int>();
    VisitDirectory(0, string.Empty, stack);
  }

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<RarcEntry> Entries => this._entries;

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(RarcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new InvalidOperationException("Directory entries do not have payload bytes.");
    if (entry.Size > int.MaxValue)
      throw new InvalidDataException("RARC entry is too large for an in-memory extraction.");
    ValidateRange(entry.Offset, entry.Size, $"RARC entry '{entry.Name}'");
    var result = new byte[(int)entry.Size];
    if (result.Length > 0)
      ReadAt(entry.Offset, result, $"RARC entry '{entry.Name}'");
    return result;
  }

  /// <summary>
  /// Computes the name hash for the supplied data.
  /// </summary>
public static ushort CalculateNameHash(string name) {
    ArgumentNullException.ThrowIfNull(name);
    ushort hash = 0;
    foreach (var ch in name) {
      if (ch > 0x7F)
        throw new ArgumentException("RARC hash helper accepts ASCII names only.", nameof(name));
      hash = unchecked((ushort)(hash * 3 + ch));
    }
    return hash;
  }

  private void VisitDirectory(int nodeIndex, string prefix, HashSet<int> recursionStack) {
    if ((uint)nodeIndex >= (uint)this._nodes.Count)
      throw new InvalidDataException($"RARC references missing directory node {nodeIndex}.");
    if (!recursionStack.Add(nodeIndex))
      throw new InvalidDataException("RARC directory graph contains a cycle.");

    var node = this._nodes[nodeIndex];
    var first = checked((int)node.FirstEntryIndex);
    var end = checked(first + node.EntryCount);
    for (var index = first; index < end; ++index) {
      var record = this._fileRecords[index];
      if (record.Name is "." or "..")
        continue;

      var isFile = (record.Attributes & RarcEntryAttributes.File) != 0;
      var isDirectory = (record.Attributes & RarcEntryAttributes.Directory) != 0;
      if (isFile == isDirectory)
        throw new InvalidDataException($"RARC entry '{record.Name}' has an invalid file/directory attribute combination.");

      var fullName = prefix.Length == 0 ? record.Name : prefix + "/" + record.Name;
      if (isDirectory) {
        if (record.DataOffset >= this._nodes.Count)
          throw new InvalidDataException($"RARC directory '{fullName}' references missing node {record.DataOffset}.");
        this._entries.Add(new RarcEntry {
          Name = fullName,
          IsDirectory = true,
          Id = record.Id,
          Attributes = record.Attributes,
          Offset = 0,
          Size = 0,
        });
        VisitDirectory(checked((int)record.DataOffset), fullName, recursionStack);
        continue;
      }

      var absoluteOffset = checked(this._fileDataOffset + record.DataOffset);
      var absoluteEnd = checked(absoluteOffset + record.DataSize);
      if (absoluteOffset < this._fileDataOffset || absoluteEnd > this._fileDataEnd)
        throw new InvalidDataException($"RARC file '{fullName}' lies outside the declared file-data section.");
      ValidateRange(absoluteOffset, record.DataSize, $"RARC file '{fullName}'");
      this._entries.Add(new RarcEntry {
        Name = fullName,
        IsDirectory = false,
        Id = record.Id,
        Attributes = record.Attributes,
        Offset = absoluteOffset,
        Size = record.DataSize,
      });
    }

    recursionStack.Remove(nodeIndex);
  }

  private string ReadName(uint offset) {
    if (offset >= this._stringTable.Length)
      throw new InvalidDataException($"RARC string-table offset 0x{offset:X} is outside the table.");
    var start = checked((int)offset);
    var end = start;
    while (end < this._stringTable.Length && this._stringTable[end] != 0)
      ++end;
    if (end == this._stringTable.Length)
      throw new InvalidDataException("RARC string-table entry is not NUL terminated.");
    return Encoding.ASCII.GetString(this._stringTable, start, end - start);
  }

  private void ValidateRange(long offset, long size, string what) {
    if (size < 0 || offset < this._baseOffset || offset > this._archiveEnd - size)
      throw new InvalidDataException($"{what} lies outside the declared RARC size.");
  }

  private void ReadAt(long offset, Span<byte> destination, string what) {
    ValidateRange(offset, destination.Length, what);
    this._stream.Position = offset;
    try {
      this._stream.ReadExactly(destination);
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException($"Unexpected end of stream while reading {what}.", ex);
    }
  }

  private void ReadAt(long offset, byte[] destination, string what)
    => ReadAt(offset, destination.AsSpan(), what);

  private sealed record NodeRecord(string Name, ushort Hash, ushort EntryCount, uint FirstEntryIndex);

  private sealed record FileRecord(
    ushort Id,
    ushort Hash,
    RarcEntryAttributes Attributes,
    string Name,
    uint DataOffset,
    uint DataSize);
}
