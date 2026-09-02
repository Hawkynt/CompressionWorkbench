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
/// Codec 0 (8-bit unsigned PCM), the three Creative ADPCM variants — codec 1
/// (4-bit), codec 2 (2.6-bit) and codec 3 (2-bit), all decoded to 16-bit signed LE
/// PCM — and codec 4 (16-bit signed LE PCM) of the legacy type-1 block, and the modern
/// type-9 block (PCM at the stated bit depth and channel count), are decoded into
/// <see cref="ParsedVoc.InterleavedPcm"/> as little-endian integer samples. The A-law
/// (codec 6) and u-law (codec 7) variants remain undecoded, so the descriptor surfaces
/// the FULL file only for those.
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

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
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
  /// 8-bit unsigned PCM (kept as-is — WAV 8-bit is also unsigned); codec 1 is Creative
  /// 4-bit ADPCM (decoded here to 16-bit signed LE PCM); codec 4 is 16-bit signed LE
  /// PCM. Any other codec marks the stream undecodable.
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
      case 1: { // Creative 4-bit ADPCM → 16-bit signed LE PCM
        WriteShorts(pcm, DecodeCreativeAdpcm(samples, codeWidth: 4));
        bitsPerSample = 16;
        anyDecodable = true;
        break;
      }
      case 2: { // Creative 2.6-bit ADPCM (3 codes/byte: 3,3,2 bits) → 16-bit signed LE PCM
        WriteShorts(pcm, DecodeCreativeAdpcm(samples, codeWidth: 26));
        bitsPerSample = 16;
        anyDecodable = true;
        break;
      }
      case 3: { // Creative 2-bit ADPCM (4 codes/byte) → 16-bit signed LE PCM
        WriteShorts(pcm, DecodeCreativeAdpcm(samples, codeWidth: 2));
        bitsPerSample = 16;
        anyDecodable = true;
        break;
      }
      case 4: // 16-bit signed LE PCM
        bitsPerSample = 16;
        pcm.Write(samples);
        anyDecodable = true;
        break;
      default: // A-law, u-law — not decoded.
        anyUndecodable = true;
        break;
    }
  }

  private static void WriteShorts(MemoryStream pcm, short[] decoded) {
    var buf = new byte[decoded.Length * 2];
    for (var i = 0; i < decoded.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2), decoded[i]);
    pcm.Write(buf);
  }

  // Creative ADPCM step-adaptation table (ffmpeg ff_adpcm_AdaptationTable, low 8 entries
  // used; the CT decoder indexes it by the 3-bit magnitude).
  private static readonly short[] CreativeAdaptation = [230, 230, 230, 230, 307, 409, 512, 614];

  /// <summary>
  /// Decodes a Creative Labs ADPCM body (VOC codecs 1, 2 and 3) to 16-bit signed PCM.
  /// <para>
  /// The first body byte is always a full 8-bit unsigned reference sample (the initial
  /// predictor, mapped to signed 16-bit). Every subsequent byte is unpacked into
  /// fixed-width codes, <b>most-significant code first</b>. Each code's top bit is the
  /// sign and its lower bits are the delta magnitude; the predictor is leaked by
  /// 254/256 and the step adapts through <see cref="CreativeAdaptation"/> — the
  /// generalisation of ffmpeg's <c>adpcm_ct_expand_nibble</c> (ADPCM_CT) to the code
  /// width. The reconstruction is <c>diff = ((2*magnitude + 1) * step) &gt;&gt; (w-1)</c>
  /// for a width-<c>w</c> code and the step is clamped to [511, 32767].
  /// </para>
  /// <para>
  /// <paramref name="codeWidth"/> selects the bit-packing:
  /// <list type="bullet">
  ///   <item><c>4</c> — codec 1: two 4-bit codes per byte (high nibble first).</item>
  ///   <item><c>26</c> — codec 2 (2.6-bit): three codes per byte of widths 3, 3 and 2
  ///     bits, top-first (bits 7-5, 4-2, 1-0). The 2.6-bit name reflects 8 bits / 3
  ///     codes ≈ 2.67 bits/sample.</item>
  ///   <item><c>2</c> — codec 3: four 2-bit codes per byte, top-first.</item>
  /// </list>
  /// </para>
  /// </summary>
  private static short[] DecodeCreativeAdpcm(ReadOnlySpan<byte> body, int codeWidth) {
    if (body.Length == 0) return [];

    var predictor = (body[0] - 128) << 8;   // 8-bit unsigned reference → signed 16-bit
    predictor = Clip16(predictor);
    var step = 0;

    var dataBytes = body.Length - 1;
    var perByte = codeWidth switch { 4 => 2, 26 => 3, 2 => 4, _ => throw new ArgumentOutOfRangeException(nameof(codeWidth)) };
    var output = new short[1 + dataBytes * perByte];
    var n = 0;
    output[n++] = (short)predictor;

    for (var i = 1; i < body.Length; ++i) {
      var v = body[i];
      switch (codeWidth) {
        case 4:
          output[n++] = ExpandCode((v >> 4) & 0x0F, 4, ref predictor, ref step);
          output[n++] = ExpandCode(v & 0x0F, 4, ref predictor, ref step);
          break;
        case 26: // three codes: 3 bits (7-5), 3 bits (4-2), 2 bits (1-0), top-first.
          output[n++] = ExpandCode((v >> 5) & 0x07, 3, ref predictor, ref step);
          output[n++] = ExpandCode((v >> 2) & 0x07, 3, ref predictor, ref step);
          output[n++] = ExpandCode(v & 0x03, 2, ref predictor, ref step);
          break;
        case 2: // four 2-bit codes, top-first.
          output[n++] = ExpandCode((v >> 6) & 0x03, 2, ref predictor, ref step);
          output[n++] = ExpandCode((v >> 4) & 0x03, 2, ref predictor, ref step);
          output[n++] = ExpandCode((v >> 2) & 0x03, 2, ref predictor, ref step);
          output[n++] = ExpandCode(v & 0x03, 2, ref predictor, ref step);
          break;
      }
    }
    return output;
  }

  /// <summary>
  /// Expands one Creative ADPCM code of <paramref name="width"/> bits. The top bit is
  /// the sign, the lower <c>width-1</c> bits the magnitude. With <paramref name="width"/>
  /// of 4 this is byte-for-byte the legacy codec-1 nibble expander; narrower widths
  /// reuse the same predictor leak and step adaptation, scaled to the code width.
  /// </summary>
  private static short ExpandCode(int code, int width, ref int predictor, ref int step) {
    var signBit = 1 << (width - 1);
    var sign = code & signBit;
    var magnitude = code & (signBit - 1);
    var diff = ((2 * magnitude + 1) * step) >> (width - 1);
    predictor = ((predictor * 254) >> 8) + (sign != 0 ? -diff : diff);
    predictor = Clip16(predictor);
    var newStep = (CreativeAdaptation[magnitude] * step) >> 8;
    step = newStep < 511 ? 511 : newStep > 32767 ? 32767 : newStep;
    return (short)predictor;
  }

  private static int Clip16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;
}
