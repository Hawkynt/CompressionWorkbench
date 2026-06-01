#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.ApplePascal;

/// <summary>
/// Reads Apple UCSD Pascal disk volumes (Apple II / Apple III / Lisa
/// Pascal, late 1970s–early 1980s). The Apple Pascal filesystem is
/// extent-based — every file is a single contiguous block range, the
/// directory holds at most 77 entries, and the entire volume directory
/// fits in 2 KB (blocks 2..5).
/// <para>
/// Volume directory header (26 bytes, starts at block 2 = offset 0x400
/// on a 512-byte-block image; little-endian throughout):
///   0x00 u16  first block of the directory (=0)
///   0x02 u16  block after directory (=6)
///   0x04 u16  entry type (0 = volume header)
///   0x06 byte volume-name length (1..7)
///   0x07 char[7] volume name (uppercased ASCII)
///   0x0E u16  total blocks on volume
///   0x10 u16  number of files (1..77)
///   0x12 u16  first block to access (cached)
///   0x14 u32  last modification date (Pascal packed format)
///   0x18 byte[4] reserved
/// </para>
/// <para>
/// File entry layout (26 bytes each, packed back-to-back after the header):
///   0x00 u16  start block
///   0x02 u16  end block (exclusive — file occupies [start..end))
///   0x04 u16  file kind (0=untyped, 1=xdsk, 2=code, 3=text, 4=info, 5=data, 6=graf, 7=foto)
///   0x06 byte filename length (1..15)
///   0x07 char[15] filename (uppercased ASCII)
///   0x16 u16  bytes used in last block (1..512)
///   0x18 u32  last modification date (Pascal packed)
/// </para>
/// </summary>
public sealed class ApplePascalReader : IDisposable {
  public const int BlockSize = 512;
  public const int DirectoryBlock = 2;
  public const int DirectoryOffset = DirectoryBlock * BlockSize;
  public const int EntrySize = 26;
  public const int MaxEntries = 77;

  private readonly byte[] _data;
  private readonly List<ApplePascalEntry> _entries = [];

  public IReadOnlyList<ApplePascalEntry> Entries => _entries;
  public bool ValidVolume { get; private set; }
  public string VolumeName { get; private set; } = "";
  public int TotalBlocks { get; private set; }
  public int FileCount { get; private set; }

  public ApplePascalReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < DirectoryOffset + EntrySize) return;

    var hdr = _data.AsSpan(DirectoryOffset, EntrySize);
    var firstBlock = BinaryPrimitives.ReadUInt16LittleEndian(hdr[..2]);
    var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(2, 2));
    var entryType = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4, 2));
    // Validate this is a volume header: type==0, first==0, next is reasonable.
    if (entryType != 0 || firstBlock != 0 || nextBlock < 6 || nextBlock > 18) return;

    var nameLen = hdr[6];
    if (nameLen is < 1 or > 7) return;
    this.VolumeName = ReadAscii(hdr.Slice(7, nameLen));

    this.TotalBlocks = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(14, 2));
    this.FileCount = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(16, 2));
    if (this.FileCount > MaxEntries) return; // Corrupt
    if (this.TotalBlocks < 6) return;        // Volume must hold the directory itself.

    this.ValidVolume = true;

    var firstEntryOffset = DirectoryOffset + EntrySize;
    for (var i = 0; i < this.FileCount; i++) {
      var entryOffset = firstEntryOffset + i * EntrySize;
      if (entryOffset + EntrySize > _data.Length) break;
      var entry = _data.AsSpan(entryOffset, EntrySize);
      var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(entry[..2]);
      var endBlock = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(2, 2));
      var kind = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(4, 2));
      var fnLen = entry[6];
      if (fnLen is < 1 or > 15) continue;
      var fname = ReadAscii(entry.Slice(7, fnLen));
      var bytesInLast = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(22, 2));
      if (startBlock >= endBlock || endBlock > this.TotalBlocks) continue;

      var blocks = endBlock - startBlock;
      var size = (blocks - 1) * BlockSize + Math.Clamp((int)bytesInLast, 1, BlockSize);
      _entries.Add(new ApplePascalEntry {
        Name = AppendKindExtension(fname, kind),
        Size = size,
        IsDirectory = false,
        StartBlock = startBlock,
        EndBlock = endBlock,
        FileKind = kind,
        BytesInLastBlock = bytesInLast,
      });
    }
  }

  private static string ReadAscii(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    for (var i = 0; i < span.Length; i++)
      chars[i] = (char)(span[i] & 0x7F);
    return new string(chars);
  }

  private static string AppendKindExtension(string baseName, int kind) {
    var ext = kind switch {
      2 => ".code",
      3 => ".text",
      4 => ".info",
      5 => ".data",
      6 => ".graf",
      7 => ".foto",
      _ => "",
    };
    return baseName + ext;
  }

  public byte[] Extract(ApplePascalEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = entry.StartBlock * BlockSize;
    if (offset < 0 || offset + entry.Size > _data.Length) return [];
    return _data.AsSpan(offset, (int)entry.Size).ToArray();
  }

  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidVolume ? "ok" : "invalid").Append('\n');
    b.Append("format=Apple UCSD Pascal Volume\n");
    b.Append(CultureInfo.InvariantCulture, $"volume_name={this.VolumeName}\n");
    b.Append(CultureInfo.InvariantCulture, $"total_blocks={this.TotalBlocks}\n");
    b.Append(CultureInfo.InvariantCulture, $"file_count={this.FileCount}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  public void Dispose() { }
}
