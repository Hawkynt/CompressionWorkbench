#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Vdfs;

public sealed class VdfsReader : IDisposable {
  private static readonly byte[] Magic = "PSVDSC_V2.00\n\r\n\r"u8.ToArray();
  private const int HeaderSize = 16;
  private const int EntrySize = 80;

  private readonly byte[] _data;
  private readonly List<VdfsEntry> _entries = [];

  public IReadOnlyList<VdfsEntry> Entries => _entries;

  public VdfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize + 20)
      throw new InvalidDataException("VDFS: file too small.");

    if (!_data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
      throw new InvalidDataException("VDFS: invalid magic.");

    var entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(16));
    var rootOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(32));

    // Entries start at rootOffset (or at offset 36 if rootOffset is 0)
    var entriesStart = rootOffset > 0 ? rootOffset : 36;

    for (int i = 0; i < entryCount; i++) {
      var off = entriesStart + i * EntrySize;
      if (off + EntrySize > _data.Length) break;

      // Name: 64 bytes, null/space terminated
      var nameEnd = off + 64;
      var nameLen = 64;
      for (int j = 0; j < 64; j++) {
        if (_data[off + j] == 0 || _data[off + j] == 0x20 && (j + 1 >= 64 || _data[off + j + 1] == 0)) {
          nameLen = j;
          break;
        }
      }
      var name = Encoding.ASCII.GetString(_data, off, nameLen).TrimEnd();

      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 64));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 68));
      var type = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 72));

      var isDir = (type & 0x01) != 0 && (type & 0x02) == 0;
      // Some VDFS implementations use: type & 0x01 for directory, rest are files
      // Use bitmask: if bit 0 set and not bit 1 -> directory

      if (string.IsNullOrEmpty(name)) continue;

      _entries.Add(new VdfsEntry {
        Name = name,
        Size = isDir ? 0 : size,
        IsDirectory = isDir,
        DataOffset = dataOffset,
      });
    }
  }

  public byte[] Extract(VdfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan((int)entry.DataOffset, (int)entry.Size).ToArray();
  }

  public void Dispose() { }
}
