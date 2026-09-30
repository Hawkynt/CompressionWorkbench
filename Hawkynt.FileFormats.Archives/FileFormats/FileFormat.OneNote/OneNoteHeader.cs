using System.Buffers.Binary;

namespace FileFormat.OneNote;

/// <summary>
/// Parsed fixed header fields used to expose intact OneNote revision-store regions.
/// The revision/object graph is deliberately not rewritten by this layer.
/// </summary>
internal readonly record struct OneNoteHeader(
  OneNoteFileType FileType,
  Guid FileId,
  Guid AncestorId,
  Guid FileFormatId,
  uint NameCrc,
  uint LastWriterVersion,
  uint OldestWriterVersion,
  uint NewestWriterVersion,
  uint OldestReaderVersion,
  ulong ExpectedFileLength,
  Guid FileVersionId,
  ulong FileVersionGeneration,
  ulong TransactionLogOffset,
  uint TransactionLogLength,
  ulong RootFileNodeListOffset,
  uint RootFileNodeListLength
) {
  // The 0x400-byte file header is followed by the first allocatable chunk;
  // the reserved header tail occupies bytes 0x128 through 0x3FF.
  internal const int Size = 0x400;
  private const int FileIdOffset = 0x10;
  private const int FileFormatIdOffset = 0x30;
  private const int AncestorIdOffset = 0x80;
  private const int NameCrcOffset = 0x90;
  private const int LastWriterVersionOffset = 0x40;
  private const int OldestWriterVersionOffset = 0x44;
  private const int NewestWriterVersionOffset = 0x48;
  private const int OldestReaderVersionOffset = 0x4C;
  private const int TransactionLogReferenceOffset = 0xA0;
  private const int RootFileNodeListReferenceOffset = 0xAC;
  private const int ExpectedFileLengthOffset = 0xC4;
  private const int FileVersionIdOffset = 0xD4;
  private const int FileVersionGenerationOffset = 0xE4;

  internal static bool TryRead(Stream stream, out OneNoteHeader header) {
    ArgumentNullException.ThrowIfNull(stream);
    header = default;
    if (!stream.CanSeek || stream.Length < Size) return false;

    var origin = stream.Position;
    Span<byte> bytes = stackalloc byte[Size];
    try {
      stream.Position = 0;
      var read = 0;
      while (read < bytes.Length) {
        var count = stream.Read(bytes[read..]);
        if (count <= 0) return false;
        read += count;
      }

      using var typeStream = new MemoryStream(bytes[..16].ToArray(), writable: false);
      var fileType = OneNoteDetector.DetectFileType(typeStream);
      if (fileType == OneNoteFileType.Unknown || !bytes.Slice(FileFormatIdOffset, 16).SequenceEqual(OneNoteDetector.Guid2007))
        return false;

      header = new OneNoteHeader(
        fileType,
        new Guid(bytes.Slice(FileIdOffset, 16)),
        new Guid(bytes.Slice(AncestorIdOffset, 16)),
        new Guid(bytes.Slice(FileFormatIdOffset, 16)),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[NameCrcOffset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[LastWriterVersionOffset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[OldestWriterVersionOffset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[NewestWriterVersionOffset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[OldestReaderVersionOffset..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[ExpectedFileLengthOffset..]),
        new Guid(bytes.Slice(FileVersionIdOffset, 16)),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[FileVersionGenerationOffset..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[TransactionLogReferenceOffset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[(TransactionLogReferenceOffset + 8)..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[RootFileNodeListReferenceOffset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[(RootFileNodeListReferenceOffset + 8)..])
      );
      return true;
    } finally {
      stream.Position = origin;
    }
  }

  internal bool HasValidReferences(long actualLength) =>
    this.ExpectedFileLength == (ulong)actualLength &&
    IsRangeValid(this.TransactionLogOffset, this.TransactionLogLength, actualLength) &&
    IsRangeValid(this.RootFileNodeListOffset, this.RootFileNodeListLength, actualLength);

  private static bool IsRangeValid(ulong offset, uint length, long totalLength) =>
    length > 0 && offset >= Size && offset <= (ulong)totalLength && length <= (ulong)totalLength - offset;
}
