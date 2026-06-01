#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Voc;

/// <summary>
/// Creative Voice File (<c>.voc</c>) parser. The 26-byte header is
/// <c>"Creative Voice File\x1A"</c> (19 ASCII chars + byte <c>0x1A</c>),
/// a little-endian uint16 data-block offset (<c>0x001A</c>), a uint16 version
/// and a uint16 checksum. After the header comes a sequence of data blocks,
/// each <c>uint8 type | uint24 (3-byte LE) length | body[length]</c>, except
/// the terminator block (type 0) which is a single byte with no length field.
/// <para>
/// Consecutive sound blocks (types 1/2/9) are concatenated into one PCM stream.
/// Codec 0 (8-bit unsigned PCM) and codec 4 (16-bit signed LE PCM) of the legacy
/// type-1 block, and the modern type-9 block (PCM at the stated bit depth and
/// channel count), are decoded into <see cref="ParsedVoc.InterleavedPcm"/> as
/// little-endian integer samples. Unsupported codecs leave the PCM null so the
/// descriptor surfaces the FULL file only.
/// </para>
/// </summary>
public sealed class VocReader {

  /// <summary>Parsed VOC: stream geometry, decoded interleaved LE PCM (or null) and any text blocks.</summary>
  public sealed record ParsedVoc(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int Codec,
    byte[]? InterleavedPcm,
    IReadOnlyList<string> TextBlocks);

  private static readonly byte[] Magic = "Creative Voice File"u8.ToArray();

  public ParsedVoc Read(ReadOnlySpan<byte> data) {
    if (data.Length < 26)
      throw new InvalidDataException("VOC too short for 26-byte header.");
    if (!data[..Magic.Length].SequenceEqual(Magic) || data[Magic.Length] != 0x1A)
      throw new InvalidDataException("Missing 'Creative Voice File' magic.");

    var dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
    if (dataOffset < 26 || dataOffset > data.Length)
      throw new InvalidDataException($"VOC data-block offset {dataOffset} out of range.");

    int pos = dataOffset;

    // Stream-wide geometry; defaults match a legacy mono 8-bit block.
    var numChannels = 1;
    var sampleRate = 0;
    var bitsPerSample = 8;
    var codec = 0;
    var anyDecodable = false;
    var anyUndecodable = false;

    // Block-8 (extended) carries rate/codec/channels for the *following* type-1 block.
    var pendingExt = false;
    var extSampleRate = 0;
    var extChannels = 1;
    var extCodec = 0;

    using var pcm = new MemoryStream();
    var texts = new List<string>();

    while (pos < data.Length) {
      var type = data[pos];
      if (type == 0) // terminator
        break;
      if (pos + 4 > data.Length)
        throw new InvalidDataException("VOC block header truncated.");
      var length = data[pos + 1] | (data[pos + 2] << 8) | (data[pos + 3] << 16);
      var bodyStart = pos + 4;
      if (bodyStart + length > data.Length)
        throw new InvalidDataException($"VOC block (type {type}) body truncated.");
      var body = data.Slice(bodyStart, length);

      switch (type) {
        case 1: { // Sound data (legacy)
          if (length < 2) break;
          var divisor = body[0];
          var blockCodec = body[1];
          var samples = body[2..];
          if (pendingExt) {
            // Preceding block-8 overrides rate/codec/channels.
            sampleRate = extSampleRate;
            numChannels = extChannels;
            codec = extCodec;
            pendingExt = false;
          } else {
            sampleRate = 1000000 / (256 - divisor);
            numChannels = 1;
            codec = blockCodec;
          }
          AppendSound(pcm, codec, samples, ref bitsPerSample, ref anyDecodable, ref anyUndecodable);
          break;
        }
        case 2: { // Continuation of previous sound block (raw samples, same params)
          AppendSound(pcm, codec, body, ref bitsPerSample, ref anyDecodable, ref anyUndecodable);
          break;
        }
        case 8: { // Extended: sets params for the following type-1 block
          if (length < 4) break;
          var timeConstant = BinaryPrimitives.ReadUInt16LittleEndian(body);
          extCodec = body[2];
          var stereoFlag = body[3];
          extChannels = stereoFlag != 0 ? 2 : 1;
          extSampleRate = (int)(256000000L / (extChannels * (65536 - timeConstant)));
          pendingExt = true;
          break;
        }
        case 9: { // Sound data (v1.20+, multi-channel / multi-bit)
          if (length < 12) break;
          sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
          var bits = body[4];
          numChannels = body[5];
          var blockCodec = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
          codec = blockCodec;
          var samples = body[12..];
          if (blockCodec == 0 && bits is 8 or 16 or 24 or 32) {
            bitsPerSample = bits;
            pcm.Write(samples);
            anyDecodable = true;
          } else
            anyUndecodable = true;
          break;
        }
        case 5: { // ASCII text
          var raw = body;
          var end = raw.Length;
          while (end > 0 && raw[end - 1] == 0) --end;
          texts.Add(Encoding.ASCII.GetString(raw[..end]));
          break;
        }
        default:
          // 3 silence, 4 marker, 6/7 repeat, others — ignored for channel extraction.
          break;
      }

      pos = bodyStart + length;
    }

    var decoded = anyDecodable && !anyUndecodable ? pcm.ToArray() : null;
    return new ParsedVoc(numChannels, sampleRate, bitsPerSample, codec, decoded, texts);
  }

  /// <summary>
  /// Appends one legacy sound-block payload to <paramref name="pcm"/>. Codec 0 is
  /// 8-bit unsigned PCM (kept as-is — WAV 8-bit is also unsigned); codec 4 is 16-bit
  /// signed LE PCM. Any other codec marks the stream undecodable.
  /// </summary>
  private static void AppendSound(
      MemoryStream pcm, int codec, ReadOnlySpan<byte> samples,
      ref int bitsPerSample, ref bool anyDecodable, ref bool anyUndecodable) {
    switch (codec) {
      case 0: // 8-bit unsigned PCM
        bitsPerSample = 8;
        pcm.Write(samples);
        anyDecodable = true;
        break;
      case 4: // 16-bit signed LE PCM
        bitsPerSample = 16;
        pcm.Write(samples);
        anyDecodable = true;
        break;
      default: // 4-bit / 2.6-bit / 2-bit ADPCM, A-law, u-law — not decoded.
        anyUndecodable = true;
        break;
    }
  }
}
