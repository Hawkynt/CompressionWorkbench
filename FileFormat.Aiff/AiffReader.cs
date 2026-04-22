#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Aiff;

/// <summary>
/// IFF/AIFF + AIFC container parser. Walks the FORM chunk chain and surfaces the
/// COMM/SSND/ANNO/MARK/INST/ID3 chunks. All integers in AIFF are big-endian; the
/// sample rate is stored as a 10-byte IEEE 754 extended-precision float.
/// <para>
/// Compression IDs recognised for AIFC:
/// <c>NONE</c>/<c>twos</c> (big-endian PCM), <c>sowt</c> (little-endian PCM),
/// <c>ulaw</c>/<c>ULAW</c> (G.711 μ-law), <c>alaw</c>/<c>ALAW</c> (G.711 A-law),
/// <c>ima4</c> (Apple/QuickTime IMA ADPCM variant), <c>fl32</c>/<c>fl64</c>
/// (IEEE float big-endian), and <c>GSM </c>.
/// </para>
/// </summary>
public sealed class AiffReader {
  public sealed record ParsedAiff(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int SampleFrames,
    string CompressionId,
    string CompressionName,
    bool IsAifc,
    byte[] SoundData,               // raw SSND body without offset/blockSize
    byte[]? Annotations,             // concatenated ANNO chunks, null if absent
    byte[]? Markers,                 // raw MARK chunk, null if absent
    byte[]? Instrument,              // raw INST chunk, null if absent
    byte[]? Id3,                     // ID3 chunk, null if absent
    IReadOnlyList<(string Id, byte[] Data)> OtherChunks);

  public ParsedAiff Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("AIFF too short for FORM header.");
    if (data[0] != 'F' || data[1] != 'O' || data[2] != 'R' || data[3] != 'M')
      throw new InvalidDataException("Missing FORM magic.");
    var isAiff = data[8] == 'A' && data[9] == 'I' && data[10] == 'F' && data[11] == 'F';
    var isAifc = data[8] == 'A' && data[9] == 'I' && data[10] == 'F' && data[11] == 'C';
    if (!isAiff && !isAifc)
      throw new InvalidDataException("FORM payload is not AIFF or AIFC.");

    var pos = 12;
    var commParsed = false;
    int numCh = 0, sampleFrames = 0, bits = 0, sampleRate = 0;
    var compId = "NONE";
    var compName = "not compressed";
    byte[]? ssnd = null;
    byte[]? annotations = null;
    byte[]? markers = null;
    byte[]? inst = null;
    byte[]? id3 = null;
    var other = new List<(string, byte[])>();

    while (pos + 8 <= data.Length) {
      var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + size > data.Length)
        throw new InvalidDataException($"Chunk '{id}' truncated.");

      switch (id) {
        case "COMM": {
          numCh = BinaryPrimitives.ReadInt16BigEndian(data[bodyStart..]);
          sampleFrames = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(bodyStart + 2)..]);
          bits = BinaryPrimitives.ReadInt16BigEndian(data[(bodyStart + 6)..]);
          sampleRate = Decode80BitFloatToInt(data.Slice(bodyStart + 8, 10));
          if (isAifc && size >= 22) {
            compId = Encoding.ASCII.GetString(data.Slice(bodyStart + 18, 4));
            // Pascal string: 1-byte length + bytes (+ optional pad to even).
            var nameLen = data[bodyStart + 22];
            compName = Encoding.ASCII.GetString(data.Slice(bodyStart + 23, Math.Min(nameLen, size - 23)));
          }
          commParsed = true;
          break;
        }
        case "SSND": {
          // SSND body: 4-byte offset + 4-byte blockSize + samples.
          var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[bodyStart..]);
          var samplesStart = bodyStart + 8 + offset;
          var samplesEnd = bodyStart + size;
          ssnd = data.Slice(samplesStart, samplesEnd - samplesStart).ToArray();
          break;
        }
        case "ANNO": {
          var chunk = data.Slice(bodyStart, size).ToArray();
          if (annotations == null) annotations = chunk;
          else {
            var combined = new byte[annotations.Length + chunk.Length + 1];
            annotations.CopyTo(combined, 0);
            combined[annotations.Length] = (byte)'\n';
            chunk.CopyTo(combined, annotations.Length + 1);
            annotations = combined;
          }
          break;
        }
        case "MARK": markers = data.Slice(bodyStart, size).ToArray(); break;
        case "INST": inst = data.Slice(bodyStart, size).ToArray(); break;
        case "ID3 ": id3 = data.Slice(bodyStart, size).ToArray(); break;
        default: other.Add((id, data.Slice(bodyStart, size).ToArray())); break;
      }
      pos = bodyStart + size + (size & 1);
    }

    if (!commParsed) throw new InvalidDataException("AIFF missing COMM chunk.");
    if (ssnd == null) ssnd = [];

    return new ParsedAiff(
      numCh, sampleRate, bits, sampleFrames, compId.TrimEnd(' ', '\0'), compName,
      isAifc, ssnd, annotations, markers, inst, id3, other);
  }

  /// <summary>
  /// Decodes the 80-bit IEEE 754 extended-precision float that AIFF uses for the
  /// sample rate field. Returns the value truncated to int; non-finite/negative
  /// inputs return 0.
  /// </summary>
  public static int Decode80BitFloatToInt(ReadOnlySpan<byte> b) {
    var sign = (b[0] & 0x80) != 0;
    var exponent = ((b[0] & 0x7F) << 8) | b[1];
    ulong mantissa = 0;
    for (var i = 0; i < 8; ++i) mantissa = (mantissa << 8) | b[2 + i];
    if (exponent == 0 && mantissa == 0) return 0;
    if (exponent == 0x7FFF) return 0;
    // Unbiased exponent.
    var e = exponent - 16383;
    if (e < 0) return 0;
    if (e > 63) return int.MaxValue;
    // Extended precision has an explicit integer bit at the top of the mantissa.
    var value = e >= 63 ? mantissa : (mantissa >> (63 - e));
    var result = (int)value;
    return sign ? -result : result;
  }
}
