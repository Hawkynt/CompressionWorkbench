#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.LhF;

/// <summary>
/// Reads LhF (LhFloppy) Amiga disk archives. Each track is independently
/// compressed with LZ77+Huffman (similar to LhA's -lh5- method).
/// Magic: "LhF\0" at offset 0, followed by track count and track headers.
/// </summary>
public sealed class LhFReader : IDisposable {
  public static readonly byte[] LhFMagic = "LhF\0"u8.ToArray();
  private const int TrackSize = 11 * 512; // Amiga DD: 11 sectors × 512 bytes

  private readonly byte[] _data;
  private readonly List<LhFEntry> _entries = [];

  public IReadOnlyList<LhFEntry> Entries => _entries;

  public LhFReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("LhF: file too small.");

    if (_data[0] != (byte)'L' || _data[1] != (byte)'h' || _data[2] != (byte)'F' || _data[3] != 0)
      throw new InvalidDataException("LhF: invalid magic.");

    // Header: "LhF\0" (4) + uint16 BE track count (2) + uint16 BE flags (2)
    var trackCount = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(4));
    var pos = 8;

    for (var t = 0; t < trackCount && pos + 8 <= _data.Length; t++) {
      // Per track: uint16 BE track number, uint32 BE compressed size, uint16 BE checksum
      var trackNum = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pos));
      pos += 2;
      var compSize = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos));
      pos += 4;
      pos += 2; // checksum

      if (compSize < 0 || pos + compSize > _data.Length) break;

      _entries.Add(new LhFEntry {
        Name = $"track_{trackNum:D3}.raw",
        Size = TrackSize,
        CompressedSize = compSize,
        TrackNumber = trackNum,
        Offset = pos,
      });

      pos += compSize;
    }
  }

  public byte[] Extract(LhFEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.CompressedSize > _data.Length)
      throw new InvalidDataException("LhF: data extends beyond file.");

    var compressed = _data.AsSpan(entry.Offset, (int)entry.CompressedSize);

    // If compressed size equals track size, it's stored uncompressed
    if (entry.CompressedSize == entry.Size)
      return compressed.ToArray();

    // Decompress using LH5 (LZH with 8KB window)
    try {
      using var compMs = new MemoryStream(compressed.ToArray());
      var decoder = new LzhDecoder(compMs, positionBits: 13);
      return decoder.Decode((int)entry.Size);
    } catch {
      // Fallback: return compressed data as-is
      return compressed.ToArray();
    }
  }

  public void Dispose() { }
}
