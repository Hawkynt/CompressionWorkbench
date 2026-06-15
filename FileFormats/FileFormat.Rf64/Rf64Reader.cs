#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Rf64;

/// <summary>
/// Parses an RF64 / BWF (EBU 3306, Broadcast Wave) container into its
/// <c>fmt </c> geometry, interleaved little-endian PCM <c>data</c> body, and any
/// ancillary chunks (e.g. <c>bext</c>, <c>LIST</c>/<c>INFO</c>). RF64 is a RIFF
/// variant for files larger than 4 GiB: the top-level magic is <c>RF64</c> and a
/// per-chunk 32-bit size of <c>0xFFFFFFFF</c> is a sentinel meaning "the real
/// 64-bit size lives in the <c>ds64</c> chunk".
/// </summary>
public sealed class Rf64Reader {
  public sealed record ParsedRf64(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int FormatCode,
    byte[] InterleavedPcm,
    IReadOnlyList<(string Id, byte[] Data)> MetadataChunks,
    uint? ChannelMask = null);

  private const uint SizeSentinel = 0xFFFFFFFF;

  public ParsedRf64 Read(ReadOnlySpan<byte> data) {
    if (data.Length < 16)
      throw new InvalidDataException("RF64 too short for header.");
    if (data[0] != 'R' || data[1] != 'F' || data[2] != '6' || data[3] != '4')
      throw new InvalidDataException("Missing RF64 magic.");
    if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
      throw new InvalidDataException("RF64 payload is not WAVE.");

    // ds64-supplied real sizes for sentinel-marked chunks.
    long ds64DataSize = -1;
    var ds64Table = new Dictionary<string, long>(StringComparer.Ordinal);

    var pos = 12;
    int formatCode = 0, numChannels = 0, sampleRate = 0, bitsPerSample = 0;
    var fmtParsed = false;
    uint? channelMask = null;
    byte[]? rawData = null;
    var metadata = new List<(string, byte[])>();

    while (pos + 8 <= data.Length) {
      var id = System.Text.Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size32 = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;

      // Resolve the real (possibly 64-bit) body length.
      long size = size32;
      if (id == "data" && size32 == SizeSentinel) {
        if (ds64DataSize < 0)
          throw new InvalidDataException("RF64 'data' is sentinel-sized but ds64 carried no dataSize.");
        size = ds64DataSize;
      } else if (size32 == SizeSentinel && ds64Table.TryGetValue(id, out var realSize)) {
        size = realSize;
      }

      if (bodyStart + size > data.Length)
        throw new InvalidDataException($"Chunk '{id}' truncated.");
      var iSize = checked((int)size);

      switch (id) {
        case "ds64": {
          // int64 riffSize | int64 dataSize | int64 sampleCount | uint32 tableLength | table[]
          ds64DataSize = BinaryPrimitives.ReadInt64LittleEndian(data[(bodyStart + 8)..]);
          var tableLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyStart + 24)..]);
          var t = bodyStart + 28;
          for (var i = 0; i < tableLength; ++i) {
            var chunkId = System.Text.Encoding.ASCII.GetString(data.Slice(t, 4));
            var chunkSize = BinaryPrimitives.ReadInt64LittleEndian(data[(t + 4)..]);
            ds64Table[chunkId] = chunkSize;
            t += 12;
          }
          break;
        }
        case "fmt ": {
          formatCode = BinaryPrimitives.ReadUInt16LittleEndian(data[bodyStart..]);
          numChannels = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 2)..]);
          sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyStart + 4)..]);
          bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 14)..]);
          // WAVE_FORMAT_EXTENSIBLE: dwChannelMask at +20, real code 24 bytes in.
          if (formatCode == 0xFFFE && iSize >= 40) {
            channelMask = BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyStart + 20)..]);
            formatCode = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 24)..]);
          }
          fmtParsed = true;
          break;
        }
        case "data":
          rawData = data.Slice(bodyStart, iSize).ToArray();
          break;
        default:
          metadata.Add((id, data.Slice(bodyStart, iSize).ToArray()));
          break;
      }

      // Chunks are word-aligned: if size is odd, skip a pad byte.
      pos = bodyStart + iSize + (iSize & 1);
    }

    if (!fmtParsed) throw new InvalidDataException("RF64 missing 'fmt ' chunk.");
    if (rawData == null) throw new InvalidDataException("RF64 missing 'data' chunk.");

    return new ParsedRf64(numChannels, sampleRate, bitsPerSample, formatCode, rawData, metadata, channelMask);
  }
}
