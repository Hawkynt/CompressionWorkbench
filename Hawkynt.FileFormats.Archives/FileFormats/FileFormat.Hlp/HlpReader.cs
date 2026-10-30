#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Hlp;

/// <summary>One internal file of a WinHelp file: its name, where its FILEHEADER sits and its used bytes.</summary>
public sealed record HlpInternalFile(string Name, int HeaderOffset, int ReservedSpace, int UsedSpace, byte Flags);

/// <summary>
/// Reads the internal file system of a Windows Help file (<c>.hlp</c>, WinHelp 3.x/4.x, and Multimedia
/// Viewer <c>.mvb</c>): the internal directory, a B+ tree of names and offsets, and the internal files it
/// points to (<c>|SYSTEM</c>, <c>|TOPIC</c>, <c>|FONT</c>, bitmaps, baggage), returned as stored.
/// </summary>
/// <remarks>
/// Layout (little-endian), per Manfred Winterhoff's "Windows Help File Format" description that ships
/// with helpdeco:
/// <code>
/// header      UInt32 0x00035F3F  Int32 directoryStart  Int32 firstFreeBlock  Int32 entireFileSize
/// FILEHEADER  Int32 reservedSpace  Int32 usedSpace  Byte flags, then usedSpace bytes
/// BTREEHEADER UInt16 0x293B  UInt16 flags  UInt16 pageSize  Char[16] structure  Int16 0  Int16 splits
///             Int16 rootPage  Int16 -1  Int16 totalPages  Int16 levels  Int32 totalEntries, then pages
/// index page  UInt16 unused  Int16 entries  Int16 previousPage, { STRINGZ name  Int16 page }*
/// leaf page   UInt16 unused  Int16 entries  Int16 previousPage  Int16 nextPage, { STRINGZ name  Int32 offset }*
/// </code>
/// </remarks>
public sealed class HlpReader {

  public const uint Magic = 0x00035F3F;
  private const ushort BtreeMagic = 0x293B;
  private const int FileHeaderSize = 9;
  private const int BtreeHeaderSize = 38;

  private readonly byte[] _file;

  public HlpReader(byte[] file) {
    ArgumentNullException.ThrowIfNull(file);
    this._file = file;
    if (file.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
      throw new InvalidDataException("HLP: missing the 0x00035F3F magic.");
    var directoryStart = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
    var declaredSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
    if (declaredSize > file.Length)
      throw new InvalidDataException($"HLP: header declares {declaredSize} bytes, the file has {file.Length}.");

    var (dirData, _) = this.InternalFileData(directoryStart, "internal directory");
    this.Files = ReadDirectory(dirData)
      .Select(e => {
        var (_, header) = this.InternalFileData(e.Offset, e.Name);
        return header with { Name = e.Name };
      })
      .ToList();
  }

  /// <summary>Internal files in the directory's (sorted) order.</summary>
  public IReadOnlyList<HlpInternalFile> Files { get; }

  /// <summary>An internal file's used bytes.</summary>
  public byte[] Extract(HlpInternalFile file) {
    ArgumentNullException.ThrowIfNull(file);
    return this._file.AsSpan(file.HeaderOffset + FileHeaderSize, file.UsedSpace).ToArray();
  }

  private (byte[] Data, HlpInternalFile Header) InternalFileData(int offset, string what) {
    if (offset < 16 || offset > this._file.Length - FileHeaderSize)
      throw new InvalidDataException($"HLP: {what} at 0x{offset:X} lies outside the file.");
    var reserved = BinaryPrimitives.ReadInt32LittleEndian(this._file.AsSpan(offset));
    var used = BinaryPrimitives.ReadInt32LittleEndian(this._file.AsSpan(offset + 4));
    if (used < 0 || reserved < used + FileHeaderSize || used > this._file.Length - offset - FileHeaderSize)
      throw new InvalidDataException($"HLP: {what} at 0x{offset:X} claims {used} used of {reserved} reserved bytes.");
    var header = new HlpInternalFile(what, offset, reserved, used, this._file[offset + 8]);
    return (this._file.AsSpan(offset + FileHeaderSize, used).ToArray(), header);
  }

  private static List<(string Name, int Offset)> ReadDirectory(byte[] tree) {
    if (tree.Length < BtreeHeaderSize || BinaryPrimitives.ReadUInt16LittleEndian(tree) != BtreeMagic)
      throw new InvalidDataException("HLP: the internal directory is not a B+ tree.");
    int pageSize = BinaryPrimitives.ReadUInt16LittleEndian(tree.AsSpan(4));
    int rootPage = BinaryPrimitives.ReadInt16LittleEndian(tree.AsSpan(26));
    int totalPages = BinaryPrimitives.ReadInt16LittleEndian(tree.AsSpan(30));
    int levels = BinaryPrimitives.ReadInt16LittleEndian(tree.AsSpan(32));
    var totalEntries = BinaryPrimitives.ReadInt32LittleEndian(tree.AsSpan(34));
    if (pageSize < 16 || totalPages < 1 || (long)BtreeHeaderSize + (long)totalPages * pageSize > tree.Length || levels < 1 || levels > 16)
      throw new InvalidDataException($"HLP: directory B+ tree of {totalPages} pages of {pageSize} bytes and {levels} levels does not fit.");

    ReadOnlySpan<byte> Page(int index) {
      if (index < 0 || index >= totalPages)
        throw new InvalidDataException($"HLP: directory page {index} of {totalPages}.");
      return tree.AsSpan(BtreeHeaderSize + index * pageSize, pageSize);
    }

    // Down the index pages along their PreviousPage links to the first leaf.
    var page = rootPage;
    for (var level = 1; level < levels; ++level)
      page = BinaryPrimitives.ReadInt16LittleEndian(Page(page)[4..]);

    var entries = new List<(string, int)>();
    var visited = new HashSet<int>();
    while (page != -1) {
      if (!visited.Add(page))
        throw new InvalidDataException($"HLP: directory leaf pages loop at page {page}.");
      var p = Page(page);
      int count = BinaryPrimitives.ReadInt16LittleEndian(p[2..]);
      var pos = 8;
      for (var i = 0; i < count; ++i) {
        var end = p[pos..].IndexOf((byte)0);
        if (end < 0 || pos + end + 5 > p.Length)
          throw new InvalidDataException($"HLP: directory entry {i} of page {page} runs past the page.");
        var name = Encoding.Latin1.GetString(p.Slice(pos, end));
        pos += end + 1;
        entries.Add((name, BinaryPrimitives.ReadInt32LittleEndian(p[pos..])));
        pos += 4;
      }
      page = BinaryPrimitives.ReadInt16LittleEndian(p[6..]);
    }
    if (entries.Count != totalEntries)
      throw new InvalidDataException($"HLP: directory lists {entries.Count} files, its header says {totalEntries}.");
    return entries;
  }
}
