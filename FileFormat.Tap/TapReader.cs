#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Tap;

/// <summary>
/// Reads ZX Spectrum TAP tape image files.
/// TAP has no magic bytes — detection is by file extension only.
/// Structure: sequence of blocks, each preceded by a uint16 LE block length.
/// First byte of each block is a flag: 0x00 = header block, 0xFF = data block.
/// Header blocks are 19 bytes: flag + fileType + 10-byte name + dataLength(u16) + param1(u16) + param2(u16) + checksum.
/// Data blocks carry the actual file payload: flag + data + checksum.
/// </summary>
public sealed class TapReader {
  private readonly byte[] _data;
  private readonly List<TapEntry> _entries = [];

  public IReadOnlyList<TapEntry> Entries => _entries;

  public TapReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    var pos = 0;
    TapEntry? pendingHeader = null;
    var orphanIndex = 0;

    while (pos + 2 <= _data.Length) {
      var blockLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos));
      pos += 2;

      if (blockLength == 0 || pos + blockLength > _data.Length)
        break;

      var flag = _data[pos];

      if (flag == 0x00 && blockLength == 19) {
        // Header block: flag(1) + fileType(1) + name(10) + dataLen(2) + param1(2) + param2(2) + checksum(1)
        var fileType = _data[pos + 1];
        var name = Encoding.ASCII.GetString(_data, pos + 2, 10).TrimEnd(' ');
        var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos + 12));

        pendingHeader = new TapEntry {
          Name = name,
          Size = dataLen,
          FileType = fileType,
          DataOffset = -1, // filled when data block is found
        };
      } else if (flag == 0xFF) {
        // Data block: flag(1) + payload + checksum(1)
        // Payload is everything between the flag and the trailing checksum byte.
        var payloadLength = blockLength - 2; // exclude flag and checksum
        if (payloadLength < 0) payloadLength = 0;
        var dataOffset = pos + 1; // byte after the flag byte

        string entryName;
        byte entryFileType;
        int entrySize;

        if (pendingHeader != null) {
          entryName = pendingHeader.Name;
          entryFileType = pendingHeader.FileType;
          entrySize = payloadLength;
          pendingHeader = null;
        } else {
          entryName = $"BLOCK_{orphanIndex++}";
          entryFileType = 3; // Code
          entrySize = payloadLength;
        }

        _entries.Add(new TapEntry {
          Name = entryName,
          Size = entrySize,
          FileType = entryFileType,
          DataOffset = dataOffset,
        });
      } else {
        // Unknown or non-standard block — skip
        pendingHeader = null;
      }

      pos += blockLength;
    }
  }

  /// <summary>
  /// Extracts the payload of an entry (excluding flag and checksum bytes).
  /// </summary>
  public byte[] Extract(TapEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size <= 0 || entry.DataOffset < 0) return [];

    var offset = (int)entry.DataOffset;
    var length = entry.Size;
    if (offset + length > _data.Length)
      length = Math.Max(0, _data.Length - offset);
    return _data.AsSpan(offset, length).ToArray();
  }
}
