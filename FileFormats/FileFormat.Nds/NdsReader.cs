using System.Text;

namespace FileFormat.Nds;

/// <summary>
/// Reads the header and NitroFS file system from a Nintendo DS ROM image (.nds).
/// </summary>
public sealed class NdsReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  // FAT: array of (start, end) pairs indexed by file ID
  private readonly (uint Start, uint End)[] _fat;

  // Offset of FNT within ROM
  private readonly uint _fntOffset;

  // First file ID assigned to the root directory's sub-table
  private readonly ushort _rootFirstFileId;

  /// <summary>Gets the game title (up to 12 bytes, null-padded ASCII).</summary>
  public string GameTitle { get; }

  /// <summary>Gets the 4-character game code.</summary>
  public string GameCode { get; }

  /// <summary>Gets the 2-character maker code.</summary>
  public string MakerCode { get; }

  /// <summary>Gets the unit code byte.</summary>
  public byte UnitCode { get; }

  /// <summary>Gets the total ROM size in bytes as declared in the header.</summary>
  public uint RomSize { get; }

  /// <summary>Gets all file and directory entries discovered in the NitroFS.</summary>
  public IReadOnlyList<NdsEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="NdsReader"/> from a stream containing an NDS ROM image.
  /// </summary>
  /// <param name="stream">The stream containing the NDS ROM image.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public NdsReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    // Read the 4096-byte header
    Span<byte> header = stackalloc byte[0x1000];
    ReadExactAt(0, header);

    // Parse header fields
    this.GameTitle = ReadNullPaddedAscii(header[0x00..0x0C]);
    this.GameCode = Encoding.ASCII.GetString(header[0x0C..0x10]);
    this.MakerCode = Encoding.ASCII.GetString(header[0x10..0x12]);
    this.UnitCode = header[0x12];
    this.RomSize = ReadUInt32LE(header, 0x80);

    var arm9RomOffset = ReadUInt32LE(header, 0x20);
    var arm9Size = ReadUInt32LE(header, 0x2C);
    var arm7RomOffset = ReadUInt32LE(header, 0x30);
    var arm7Size = ReadUInt32LE(header, 0x3C);

    this._fntOffset = ReadUInt32LE(header, 0x40);
    var fntSize = ReadUInt32LE(header, 0x44);
    var fatOffset = ReadUInt32LE(header, 0x48);
    var fatSize = ReadUInt32LE(header, 0x4C);

    // Validate FNT/FAT are present
    if (fntSize == 0 || fatSize == 0)
      throw new InvalidDataException("NDS ROM has no NitroFS file system (FNT/FAT size is zero).");

    // Read FAT
    var fileCount = (int)(fatSize / 8);
    this._fat = new (uint, uint)[fileCount];
    var fatBytes = new byte[fatSize];
    ReadExactAt(fatOffset, fatBytes);
    for (var i = 0; i < fileCount; i++) {
      var start = BitConverter.ToUInt32(fatBytes, i * 8);
      var end = BitConverter.ToUInt32(fatBytes, i * 8 + 4);
      this._fat[i] = (start, end);
    }

    // Read FNT
    var fntBytes = new byte[fntSize];
    ReadExactAt(this._fntOffset, fntBytes);

    // The root dir entry is at FNT[0]: subtable offset (uint32), first file ID (uint16), dir count (uint16)
    this._rootFirstFileId = BitConverter.ToUInt16(fntBytes, 4);

    // Walk the directory tree
    this.Entries = BuildEntries(fntBytes);
  }

  /// <summary>
  /// Extracts the raw data for a file entry.
  /// </summary>
  /// <param name="entry">The file entry to extract. Must not be a directory.</param>
  /// <returns>The file data bytes.</returns>
  public byte[] Extract(NdsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];

    var data = new byte[entry.Size];
    ReadExactAt(entry.Offset, data);
    return data;
  }

  private List<NdsEntry> BuildEntries(byte[] fnt) {
    var entries = new List<NdsEntry>();
    // Recursively walk starting from root directory (ID 0xF000)
    WalkDirectory(fnt, 0xF000, "", entries);
    return entries;
  }

  private void WalkDirectory(byte[] fnt, ushort dirId, string parentPath, List<NdsEntry> entries) {
    // Directory entry index in the main FNT table
    var dirIndex = dirId & 0x0FFF; // root = 0, subdirs = 1, 2, ...
    var dirEntryOffset = dirIndex * 8;

    if (dirEntryOffset + 8 > fnt.Length)
      return;

    var subTableOffset = BitConverter.ToUInt32(fnt, dirEntryOffset);
    var firstFileId = BitConverter.ToUInt16(fnt, dirEntryOffset + 4);
    // bytes 6-7: parent dir ID (unused here)

    if (subTableOffset >= (uint)fnt.Length)
      return;

    // Add a directory entry for non-root directories
    if (dirId != 0xF000 && parentPath.Length > 0) {
      // The directory itself was added by the parent; we just recurse into it.
    }

    // Walk the sub-table
    var pos = (int)subTableOffset;
    var fileId = (int)firstFileId;

    while (pos < fnt.Length) {
      var lenByte = fnt[pos];
      pos++;

      if (lenByte == 0x00)
        break; // end of sub-table

      var isSubDir = (lenByte & 0x80) != 0;
      var nameLen = lenByte & 0x7F;

      if (pos + nameLen > fnt.Length)
        break;

      var name = Encoding.ASCII.GetString(fnt, pos, nameLen);
      pos += nameLen;

      var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;

      if (isSubDir) {
        // Followed by a uint16 subdirectory ID
        if (pos + 2 > fnt.Length)
          break;
        var subDirId = BitConverter.ToUInt16(fnt, pos);
        pos += 2;

        // Add directory entry
        entries.Add(new NdsEntry {
          Name = name,
          FullPath = fullPath,
          IsDirectory = true,
          FileId = -1,
          Offset = 0,
          Size = 0,
        });

        // Recurse
        WalkDirectory(fnt, subDirId, fullPath, entries);
      } else {
        // File entry
        long offset = 0;
        long size = 0;
        if (fileId < this._fat.Length) {
          var (start, end) = this._fat[fileId];
          offset = start;
          size = end > start ? end - start : 0;
        }

        entries.Add(new NdsEntry {
          Name = name,
          FullPath = fullPath,
          IsDirectory = false,
          FileId = fileId,
          Offset = offset,
          Size = size,
        });

        fileId++;
      }
    }
  }

  private void ReadExactAt(long offset, Span<byte> buffer) {
    this._stream.Position = offset;
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of NDS ROM stream.");
      totalRead += read;
    }
  }

  private static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset) =>
    BitConverter.ToUInt32(data[offset..(offset + 4)]);

  private static string ReadNullPaddedAscii(ReadOnlySpan<byte> data) {
    var len = data.IndexOf((byte)0);
    if (len < 0) len = data.Length;
    return Encoding.ASCII.GetString(data[..len]);
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
