#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Wav;

/// <summary>
/// RIFF/WAVE header + per-channel PCM extraction. Supports format codes:
/// <list type="bullet">
///   <item>1 — linear PCM (8/16/24/32-bit).</item>
///   <item>3 — IEEE float (32-bit / 64-bit).</item>
///   <item>6 — G.711 A-law (decoded to 16-bit LE PCM via <c>Codec.ALaw</c>).</item>
///   <item>7 — G.711 μ-law (decoded to 16-bit LE PCM via <c>Codec.MuLaw</c>).</item>
///   <item>0x0002 — Microsoft ADPCM (decoded via <c>Codec.MsAdpcm</c>).</item>
///   <item>0x0011 — IMA ADPCM (decoded via <c>Codec.ImaAdpcm</c>).</item>
///   <item>0x0031 — GSM 06.10 full-rate (decoded via <c>Codec.Gsm610</c>).</item>
///   <item>0xFFFE — WAVEFORMAT_EXTENSIBLE, real sub-format at +24 in <c>fmt</c> body.</item>
/// </list>
/// After decoding, <see cref="ParsedWav.InterleavedPcm"/> always holds little-endian
/// integer samples and <see cref="ParsedWav.BitsPerSample"/> reflects the decoded
/// width, so downstream callers (e.g. <c>WavFormatDescriptor</c>) see PCM regardless
/// of the on-wire compression. <see cref="ParsedWav.FormatCode"/> also reflects the
/// post-decode code (always 1 when we decoded).
/// <para>Reads only the <c>fmt</c> and <c>data</c> chunks; skips metadata chunks but
/// leaves them addressable via <see cref="ParsedWav.MetadataChunks"/>.</para>
/// </summary>
public sealed class WavReader {
  public sealed record ParsedWav(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int FormatCode,
    byte[] InterleavedPcm,
    IReadOnlyList<(string Id, byte[] Data)> MetadataChunks);

  public ParsedWav Read(ReadOnlySpan<byte> data) {
    if (data.Length < 44)
      throw new InvalidDataException("WAV too short for RIFF header + fmt/data chunks.");
    if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
      throw new InvalidDataException("Missing RIFF magic.");
    if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
      throw new InvalidDataException("RIFF payload is not WAVE.");

    var pos = 12;
    int formatCode = 0, numChannels = 0, sampleRate = 0, bitsPerSample = 0, blockAlign = 0;
    var fmtParsed = false;
    byte[]? rawData = null;
    var metadata = new List<(string, byte[])>();

    while (pos + 8 <= data.Length) {
      var id = System.Text.Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + size > data.Length)
        throw new InvalidDataException($"Chunk '{id}' truncated.");

      switch (id) {
        case "fmt ": {
          formatCode = BinaryPrimitives.ReadUInt16LittleEndian(data[bodyStart..]);
          numChannels = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 2)..]);
          sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyStart + 4)..]);
          blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 12)..]);
          bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 14)..]);
          // WAVE_FORMAT_EXTENSIBLE: real code lives 24 bytes in.
          if (formatCode == 0xFFFE && size >= 40)
            formatCode = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 24)..]);
          fmtParsed = true;
          break;
        }
        case "data":
          rawData = data.Slice(bodyStart, size).ToArray();
          break;
        default:
          metadata.Add((id, data.Slice(bodyStart, size).ToArray()));
          break;
      }
      // Chunks are word-aligned: if size is odd, skip a pad byte.
      pos = bodyStart + size + (size & 1);
    }

    if (!fmtParsed) throw new InvalidDataException("WAV missing 'fmt ' chunk.");
    if (rawData == null) throw new InvalidDataException("WAV missing 'data' chunk.");

    // Dispatch compressed formats → linear LE PCM.
    switch (formatCode) {
      case 6: { // A-law
        var shorts = Codec.ALaw.ALawCodec.Decode(rawData);
        return new ParsedWav(numChannels, sampleRate, 16, FormatCode: 1,
          InterleavedPcm: ShortsToLePcm(shorts), MetadataChunks: metadata);
      }
      case 7: { // μ-law
        var shorts = Codec.MuLaw.MuLawCodec.Decode(rawData);
        return new ParsedWav(numChannels, sampleRate, 16, FormatCode: 1,
          InterleavedPcm: ShortsToLePcm(shorts), MetadataChunks: metadata);
      }
      case 0x0011: { // IMA ADPCM
        if (blockAlign <= 0) throw new InvalidDataException("IMA ADPCM needs blockAlign.");
        var perChannel = Codec.ImaAdpcm.ImaAdpcmCodec.Decode(rawData, blockAlign, numChannels);
        return new ParsedWav(numChannels, sampleRate, 16, FormatCode: 1,
          InterleavedPcm: InterleaveChannels(perChannel), MetadataChunks: metadata);
      }
      case 0x0002: { // MS ADPCM
        if (blockAlign <= 0) throw new InvalidDataException("MS ADPCM needs blockAlign.");
        var perChannel = Codec.MsAdpcm.MsAdpcmCodec.Decode(rawData, blockAlign, numChannels);
        return new ParsedWav(numChannels, sampleRate, 16, FormatCode: 1,
          InterleavedPcm: InterleaveChannels(perChannel), MetadataChunks: metadata);
      }
      case 0x0031: { // GSM 06.10
        var shorts = Codec.Gsm610.Gsm610Codec.Decode(rawData, numChannels);
        return new ParsedWav(numChannels, sampleRate, 16, FormatCode: 1,
          InterleavedPcm: ShortsToLePcm(shorts), MetadataChunks: metadata);
      }
      default:
        return new ParsedWav(numChannels, sampleRate, bitsPerSample, formatCode, rawData, metadata);
    }
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static byte[] InterleaveChannels(short[][] perChannel) {
    if (perChannel.Length == 0) return [];
    var ch = perChannel.Length;
    var frames = perChannel[0].Length;
    var pcm = new byte[frames * ch * 2];
    for (var f = 0; f < frames; ++f) {
      for (var c = 0; c < ch; ++c) {
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * ch + c) * 2), perChannel[c][f]);
      }
    }
    return pcm;
  }
}
