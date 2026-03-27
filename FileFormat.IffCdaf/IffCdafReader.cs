#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.IffCdaf;

/// <summary>
/// Reads IFF CDAF (Compact Disk Archive Format) archives.
/// IFF-based container with FORM/CDAF header, FNAM (filename) and FDAT (data) chunks.
/// </summary>
public sealed class IffCdafReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<IffCdafEntry> _entries = [];

  public IReadOnlyList<IffCdafEntry> Entries => _entries;

  public IffCdafReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 12)
      throw new InvalidDataException("IFF-CDAF: file too small.");

    // FORM header
    if (_data[0] != (byte)'F' || _data[1] != (byte)'O' ||
        _data[2] != (byte)'R' || _data[3] != (byte)'M')
      throw new InvalidDataException("IFF-CDAF: missing FORM header.");

    var formSize = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(4));

    // CDAF type
    if (_data[8] != (byte)'C' || _data[9] != (byte)'D' ||
        _data[10] != (byte)'A' || _data[11] != (byte)'F')
      throw new InvalidDataException("IFF-CDAF: not a CDAF archive.");

    var pos = 12;
    var limit = Math.Min(8 + formSize, _data.Length);
    string? currentName = null;

    while (pos + 8 <= limit) {
      var chunkId = Encoding.ASCII.GetString(_data, pos, 4);
      var chunkSize = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos + 4));
      pos += 8;

      if (chunkSize < 0 || pos + chunkSize > _data.Length) break;

      switch (chunkId) {
        case "FNAM": {
          // Null-terminated filename
          var nameEnd = Array.IndexOf(_data, (byte)0, pos, chunkSize);
          var nameLen = nameEnd >= 0 ? nameEnd - pos : chunkSize;
          currentName = Encoding.ASCII.GetString(_data, pos, nameLen);
          break;
        }
        case "FDAT": {
          _entries.Add(new IffCdafEntry {
            Name = currentName ?? $"file_{_entries.Count}",
            Size = chunkSize,
            Offset = pos,
          });
          currentName = null;
          break;
        }
      }

      pos += chunkSize;
      // IFF chunks are word-aligned (2 bytes)
      if ((chunkSize & 1) != 0 && pos < limit) pos++;
    }
  }

  public byte[] Extract(IffCdafEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.Size > _data.Length)
      throw new InvalidDataException("IFF-CDAF: data extends beyond file.");
    return _data.AsSpan(entry.Offset, (int)entry.Size).ToArray();
  }

  public void Dispose() { }
}
