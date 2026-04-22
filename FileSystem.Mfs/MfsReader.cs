#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Mfs;

public sealed class MfsReader : IDisposable {
  private const ushort MfsMagic = 0xD2D7;
  private const int MdbOffset = 1024;

  private readonly byte[] _data;
  private readonly List<MfsEntry> _entries = [];
  private uint _blockSize;
  private int _firstBlockOffset;

  public IReadOnlyList<MfsEntry> Entries => _entries;

  public MfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < MdbOffset + 128)
      throw new InvalidDataException("MFS: image too small.");

    var sig = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset));
    if (sig != MfsMagic)
      throw new InvalidDataException("MFS: invalid signature.");

    var numAllocBlocks = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset + 18));
    _blockSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(MdbOffset + 20));
    if (_blockSize == 0) _blockSize = 1024;
    var firstAllocBlock = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset + 28));
    _firstBlockOffset = firstAllocBlock * 512;

    // File directory starts after MDB. In MFS the file directory
    // occupies the sectors between the MDB and the first allocation block.
    var dirStart = MdbOffset + 128; // approximate start
    var dirEnd = _firstBlockOffset;
    if (dirEnd <= dirStart || dirEnd > _data.Length) dirEnd = _data.Length;

    // Parse file directory entries
    var pos = dirStart;
    while (pos + 40 < dirEnd) {
      var flags = _data[pos];
      if (flags == 0) break; // end of directory

      // Skip deleted entries (bit 7 set = in use for MFS, 0 = deleted in some variants)
      // Actually in MFS, flags byte: bit 7 = 1 means file is in use
      if ((flags & 0x80) == 0) {
        // Try to skip to next entry — we need nameLength at offset 38
        if (pos + 39 < dirEnd) {
          var nl = _data[pos + 38];
          var entryLen = 39 + nl;
          if ((entryLen & 1) != 0) entryLen++;
          pos += Math.Max(entryLen, 2);
          continue;
        }
        break;
      }

      if (pos + 39 > dirEnd) break;

      var dataFirstBlock = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pos + 26));
      var dataSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(pos + 28));
      var nameLen = _data[pos + 38];

      if (pos + 39 + nameLen > dirEnd) break;
      var name = Encoding.ASCII.GetString(_data, pos + 39, nameLen);

      _entries.Add(new MfsEntry {
        Name = name,
        Size = dataSize,
        FirstBlock = dataFirstBlock,
      });

      var totalLen = 39 + nameLen;
      if ((totalLen & 1) != 0) totalLen++;
      pos += totalLen;
    }
  }

  public byte[] Extract(MfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];
    var offset = _firstBlockOffset + (int)(entry.FirstBlock * _blockSize);
    if (offset < 0 || offset >= _data.Length) return [];
    var len = (int)Math.Min(entry.Size, _data.Length - offset);
    return _data.AsSpan(offset, len).ToArray();
  }

  public void Dispose() { }
}
