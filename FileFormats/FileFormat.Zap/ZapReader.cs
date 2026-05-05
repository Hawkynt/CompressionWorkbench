#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Zap;

/// <summary>
/// Reads ZAP (Zap disk archiver) Amiga disk images.
/// Each track is independently compressed with LZ77+RLE using a backward bitstream.
/// Magic: "ZAP\0" at offset 0.
/// </summary>
public sealed class ZapReader : IDisposable {
  public static readonly byte[] ZapMagic = "ZAP\0"u8.ToArray();
  private const int TrackSize = 11 * 512; // Amiga DD: 11 sectors × 512 bytes

  private readonly byte[] _data;
  private readonly List<ZapEntry> _entries = [];

  public IReadOnlyList<ZapEntry> Entries => _entries;

  public ZapReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("ZAP: file too small.");

    if (_data[0] != (byte)'Z' || _data[1] != (byte)'A' || _data[2] != (byte)'P' || _data[3] != 0)
      throw new InvalidDataException("ZAP: invalid magic.");

    var trackCount = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(4));
    var pos = 8;

    for (var t = 0; t < trackCount && pos + 6 <= _data.Length; t++) {
      var trackNum = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pos));
      pos += 2;
      var compSize = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos));
      pos += 4;

      if (compSize < 0 || pos + compSize > _data.Length) break;

      var isCompressed = compSize < TrackSize;

      _entries.Add(new ZapEntry {
        Name = $"track_{trackNum:D3}.raw",
        Size = TrackSize,
        CompressedSize = compSize,
        TrackNumber = trackNum,
        Offset = pos,
        IsCompressed = isCompressed,
      });

      pos += compSize;
    }
  }

  public byte[] Extract(ZapEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.CompressedSize > _data.Length)
      throw new InvalidDataException("ZAP: data extends beyond file.");

    var compressed = _data.AsSpan(entry.Offset, (int)entry.CompressedSize);

    if (!entry.IsCompressed)
      return compressed.ToArray();

    // ZAP uses backward bitstream LZ77+RLE decompression
    return DecompressTrack(compressed.ToArray(), TrackSize);
  }

  private static byte[] DecompressTrack(byte[] src, int outSize) {
    var output = new byte[outSize];
    var si = src.Length - 1; // read backward
    var di = 0;
    uint bits = 0;
    var bitCount = 0;

    uint GetBit() {
      if (bitCount == 0) {
        bits = si >= 0 ? src[si--] : 0u;
        bitCount = 8;
      }
      bitCount--;
      var bit = (bits >> bitCount) & 1;
      return bit;
    }

    uint GetBits(int n) {
      uint val = 0;
      for (var i = 0; i < n; i++)
        val = (val << 1) | GetBit();
      return val;
    }

    while (di < outSize && si >= -1) {
      if (GetBit() == 0) {
        // Literal byte
        output[di++] = (byte)GetBits(8);
      } else {
        // Match or RLE
        if (GetBit() == 0) {
          // Short match: offset in 8 bits, length 2-5
          var length = (int)GetBits(2) + 2;
          var offset = (int)GetBits(8);
          if (offset == 0) break; // end marker
          for (var i = 0; i < length && di < outSize; i++) {
            output[di] = output[di - offset];
            di++;
          }
        } else {
          // Long match: offset in 12 bits, length 3-18
          var length = (int)GetBits(4) + 3;
          var offset = (int)GetBits(12);
          if (offset == 0) break; // end marker
          for (var i = 0; i < length && di < outSize; i++) {
            output[di] = output[di - offset];
            di++;
          }
        }
      }
    }

    return output;
  }

  public void Dispose() { }
}
