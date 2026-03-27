#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.Ampk;

/// <summary>
/// Reads AMPK (Amiga Pack) archives. Uses LZHUF compression (similar to LhA).
/// Format: "AMPK" magic, then file entries with 4-byte name length, name, sizes,
/// and LZH compressed data.
/// </summary>
public sealed class AmpkReader : IDisposable {
  public static readonly byte[] AmpkMagic = "AMPK"u8.ToArray();

  private readonly byte[] _data;
  private readonly List<AmpkEntry> _entries = [];

  public IReadOnlyList<AmpkEntry> Entries => _entries;

  public AmpkReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("AMPK: file too small.");

    if (_data[0] != (byte)'A' || _data[1] != (byte)'M' || _data[2] != (byte)'P' || _data[3] != (byte)'K')
      throw new InvalidDataException("AMPK: invalid magic.");

    var pos = 4;
    // File count (BE uint32)
    if (pos + 4 > _data.Length) return;
    var fileCount = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos));
    pos += 4;

    for (var i = 0; i < fileCount && pos + 4 <= _data.Length; i++) {
      // Name length (BE uint32)
      var nameLen = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos));
      pos += 4;
      if (nameLen < 0 || pos + nameLen > _data.Length) break;

      var name = Encoding.ASCII.GetString(_data, pos, nameLen);
      pos += nameLen;

      if (pos + 8 > _data.Length) break;
      var origSize = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos));
      pos += 4;
      var compSize = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos));
      pos += 4;

      if (compSize < 0 || pos + compSize > _data.Length) break;

      _entries.Add(new AmpkEntry {
        Name = name,
        Size = origSize,
        CompressedSize = compSize,
        Offset = pos,
      });

      pos += compSize;
    }
  }

  public byte[] Extract(AmpkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.CompressedSize > _data.Length)
      throw new InvalidDataException("AMPK: data extends beyond file.");

    if (entry.CompressedSize == entry.Size)
      return _data.AsSpan(entry.Offset, (int)entry.CompressedSize).ToArray();

    try {
      using var compMs = new MemoryStream(_data, entry.Offset, (int)entry.CompressedSize);
      var decoder = new LzhDecoder(compMs, positionBits: 13);
      return decoder.Decode((int)entry.Size);
    } catch {
      return _data.AsSpan(entry.Offset, (int)entry.CompressedSize).ToArray();
    }
  }

  public void Dispose() { }
}
