using System.Buffers.Binary;

namespace Codec.WavPack;

/// <summary>
/// WavPack streams that carry their source file's wrapper — the bytes of the WAV file
/// before and after its sample data, in the <c>ID_RIFF_HEADER</c> (0x21) and
/// <c>ID_RIFF_TRAILER</c> (0x22) metadata sub-blocks — so the exact original file can be
/// rebuilt. This is what <c>wavpack</c> writes for a .wav input and what WinZip stores for
/// ZIP compression method 97.
/// </summary>
/// <remarks>
/// Sub-block framing per the WavPack file format: id byte (bit 6 odd length, bit 7
/// three-byte size), size in 16-bit words, payload padded to even length. Wrapper data
/// is optional metadata, so readers that do not want it skip it.
/// </remarks>
public static class WavPackWrapper {

  private const int HeaderSize = 32;
  private const int IdRiffHeader = 0x21;
  private const int IdRiffTrailer = 0x22;

  /// <summary>
  /// Decodes a wrapped WavPack stream back to the original file: wrapper header, the
  /// samples in the file's own byte layout, wrapper trailer.
  /// </summary>
  /// <exception cref="InvalidDataException">No wrapper header, or a malformed stream.</exception>
  public static byte[] Unpack(byte[] wv) {
    var (header, trailer) = ReadWrapper(wv);
    if (header.Length == 0) throw new InvalidDataException("WavPack: the stream carries no wrapper header.");
    using var pcm = new MemoryStream();
    WavPackCodec.Decompress(new MemoryStream(wv, writable: false), pcm);
    var result = new byte[header.Length + pcm.Length + trailer.Length];
    header.CopyTo(result, 0);
    pcm.GetBuffer().AsSpan(0, (int)pcm.Length).CopyTo(result.AsSpan(header.Length));
    trailer.CopyTo(result, header.Length + (int)pcm.Length);
    return result;
  }

  /// <summary>Collects the wrapper header and trailer bytes from every block's metadata.</summary>
  public static (byte[] Header, byte[] Trailer) ReadWrapper(ReadOnlySpan<byte> wv) {
    using var header = new MemoryStream();
    using var trailer = new MemoryStream();
    var pos = 0;
    while (pos + HeaderSize <= wv.Length) {
      if (!wv.Slice(pos, 4).SequenceEqual("wvpk"u8)) {
        ++pos;
        continue;
      }
      var blockSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(wv[(pos + 4)..]) + 8;
      if (blockSize < HeaderSize || pos + blockSize > wv.Length) throw new InvalidDataException("WavPack: block extends past the stream.");
      var body = wv.Slice(pos + HeaderSize, (int)blockSize - HeaderSize);
      var o = 0;
      while (o < body.Length) {
        var id = body[o++];
        int size;
        if ((id & 0x80) != 0) {
          if (o + 3 > body.Length) break;
          size = (body[o] | (body[o + 1] << 8) | (body[o + 2] << 16)) << 1;
          o += 3;
        } else {
          if (o >= body.Length) break;
          size = body[o++] << 1;
        }
        var padded = size;
        if ((id & 0x40) != 0) --size;
        if (o + padded > body.Length) throw new InvalidDataException("WavPack: metadata sub-block past the block.");
        var rawId = id & 0x3F;
        if (rawId == IdRiffHeader) header.Write(body.Slice(o, size));
        else if (rawId == IdRiffTrailer) trailer.Write(body.Slice(o, size));
        o += padded;
      }
      pos += (int)blockSize;
    }
    return (header.ToArray(), trailer.ToArray());
  }

  /// <summary>
  /// Encodes a PCM WAV file as a wrapped WavPack stream that <see cref="Unpack"/> (and the
  /// reference <c>wvunpack</c>) turns back into the identical file.
  /// </summary>
  /// <exception cref="NotSupportedException">Not an integer-PCM WAV file.</exception>
  public static byte[] Pack(ReadOnlySpan<byte> wav) {
    var (dataStart, dataLength, channels, sampleRate, bits) = ParseWav(wav);
    var bytesPerSample = (bits + 7) / 8;
    if (bytesPerSample is < 1 or > 4) throw new NotSupportedException($"WavPack: {bits}-bit samples are not supported.");
    var frame = bytesPerSample * channels;
    var usable = dataLength - dataLength % frame;

    using var wv = new MemoryStream();
    WavPackCodec.Compress(new MemoryStream(wav.Slice(dataStart, usable).ToArray()), wv, channels, sampleRate, bytesPerSample * 8);
    var stream = wv.ToArray();

    var header = wav[..dataStart].ToArray();
    var trailer = wav[(dataStart + usable)..].ToArray();
    var blocks = SplitBlocks(stream);
    blocks[0] = InsertMetadata(blocks[0], IdRiffHeader, header);
    if (trailer.Length > 0)
      blocks[^1] = InsertMetadata(blocks[^1], IdRiffTrailer, trailer);
    using var result = new MemoryStream(stream.Length + header.Length + trailer.Length + 16);
    foreach (var b in blocks) result.Write(b);
    return result.ToArray();
  }

  private static (int DataStart, int DataLength, int Channels, int SampleRate, int Bits) ParseWav(ReadOnlySpan<byte> wav) {
    if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
      throw new NotSupportedException("WavPack wrapping needs a RIFF/WAVE file.");
    int channels = 0, rate = 0, bits = 0;
    var pos = 12;
    while (pos + 8 <= wav.Length) {
      var id = wav.Slice(pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(pos + 4)..]);
      var body = pos + 8;
      if (id.SequenceEqual("fmt "u8)) {
        if (size < 16 || body + 16 > wav.Length) throw new NotSupportedException("WAV: truncated fmt chunk.");
        var format = BinaryPrimitives.ReadUInt16LittleEndian(wav[body..]);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 2)..]);
        rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav[(body + 4)..]);
        bits = BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 14)..]);
        var pcm = format == 1 || (format == 0xFFFE && size >= 40 && body + 26 <= wav.Length
                                  && BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 24)..]) == 1);
        if (!pcm) throw new NotSupportedException("WavPack wrapping supports integer PCM WAV files only.");
      } else if (id.SequenceEqual("data"u8)) {
        if (channels == 0) throw new NotSupportedException("WAV: data chunk before fmt chunk.");
        var length = (int)Math.Min(size, (uint)(wav.Length - body));
        return (body, length, channels, rate, bits);
      }
      pos = (int)Math.Min(wav.Length, body + (long)size + (size & 1));
    }
    throw new NotSupportedException("WAV: no data chunk.");
  }

  private static List<byte[]> SplitBlocks(byte[] stream) {
    var blocks = new List<byte[]>();
    var pos = 0;
    while (pos + HeaderSize <= stream.Length) {
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(pos + 4)) + 8;
      blocks.Add(stream.AsSpan(pos, size).ToArray());
      pos += size;
    }
    return blocks;
  }

  /// <summary>Inserts a metadata sub-block right after the block header and fixes the block size.</summary>
  private static byte[] InsertMetadata(byte[] block, int id, byte[] payload) {
    var words = (payload.Length + 1) / 2;
    var large = words > 255;
    var odd = (payload.Length & 1) != 0;
    var sub = new byte[(large ? 4 : 2) + words * 2];
    sub[0] = (byte)(id | (odd ? 0x40 : 0) | (large ? 0x80 : 0));
    if (large) {
      sub[1] = (byte)words;
      sub[2] = (byte)(words >> 8);
      sub[3] = (byte)(words >> 16);
    } else
      sub[1] = (byte)words;
    payload.CopyTo(sub, large ? 4 : 2);

    var result = new byte[block.Length + sub.Length];
    block.AsSpan(0, HeaderSize).CopyTo(result);
    sub.CopyTo(result, HeaderSize);
    block.AsSpan(HeaderSize).CopyTo(result.AsSpan(HeaderSize + sub.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
    return result;
  }
}
