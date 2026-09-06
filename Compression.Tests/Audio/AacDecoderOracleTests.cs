using Codec.Aac;

namespace Compression.Tests.Audio;

/// <summary>
/// An AAC-LC bitstream produced by ffmpeg 9.0.1 (libavcodec 63.1.101) together with the
/// deterministic PCM it was encoded from, shared by the codec-level and WAVE-route oracles.
/// </summary>
/// <remarks>
/// The signal is a 440 Hz + 1970 Hz tone bed carrying one 3.1 kHz exponentially decaying
/// click, which makes the encoder switch through all four window sequences: ONLY_LONG,
/// LONG_START, EIGHT_SHORT and LONG_STOP. ffmpeg's own decoder reconstructs the source at
/// 21.6 dB SNR from this stream at 96 kbit/s; anything materially below that is our defect,
/// not the encoder's rate limit.
/// </remarks>
internal static class FfmpegAacVector {

  /// <summary>Sample rate of <see cref="Source"/> and of the encoded stream.</summary>
  public const int SampleRate = 44_100;

  /// <summary>Samples the encoder emits before the first source sample appears.</summary>
  public const int EncoderDelay = 1_024;

  /// <summary>ffmpeg's AAC-LC ADTS stream, 96 kbit/s mono.</summary>
  public const string AdtsBase64 =
    "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" +
    "WdxdHr6bUuTTqI9vMPwMtDm+BMoVuXW2FLTascOLwLjvVstQuH2perWtmO/WYi6gjbTg//FQQCU//ADmN65w3QwLQwHVK7f/" +
    "8t/+3/dPiZrP6//T//11ckYxe5T/qSqlSiA+4/cvWfr1y087MmEUpSL+3P6nDROfrlm6bYy+R864x0luHY1y6SpmLTDBrZxa" +
    "yaqpGNts1MUWbKQVokgEsHePZZrtm2xpf92ZLq2HyDDu56XRztbt+1nZk6TzvQqpftf6xtzlcnbbDh63YE/a6n6a2lk8K5oZ" +
    "vHT6llrpmRv704qiTvMz7HvMRKW1bZslnenhih4HXFiqhT2sk8EOPL46lSeOnTVwLnySaFnlWnEpRr7N9kuKVRwkHILuRECy" +
    "RRI9aCrQ2JXGFnKJj4UpTL5z1RuLAm/ACxYW2gXGbSQahSJTk0JhYEHqvGzk6ctjWuQM4L5gEZAQuhL4//FQQCc//ADqN65Q" +
    "fRMbQsTRsLQsHRMzn/+79v/n/muL41lf3/+n//fUVpt9ZKlMqRulzEqhTNlRrSUex8hlkCf4OWgvPCjKvS948fe2R9tmgvm/" +
    "1TG1UxtnG8qRqqmYM0xlGMZRzIxoMq7W35/VFRagFHV831GMC4PC3N1h8ra1VzZcnUM8ra5W/gkyHsYthFnJsrgjV40m77Gk" +
    "TUd7bZmXXV8tw5qMpt7fCWK38p5PW9n465+5Z/J2VkusrrdakV1/w8Gkt8OuefttwxfVTPppw1d8zrhUU/KM8T62Pmvbwujk" +
    "Tk10ymRBPOKwa69Pv3HWKX+DXU2phT78MIPbKvCKputyLZZw9ccd02Wi49feRC6OVSRfQFihbIkho+ja0x8lampScInCa1mY" +
    "yWaq+/vuxKloNysjgP/xUEAnn/wA6jeuUGY6EYuhQWhQOiY9f/1M//9da1dXM/p/8f/w44olTd0pllSo+9xVUMxZ9sLDcOpV" +
    "T/nZ8dFwQzYj3X1G3FRt9XDcVKzdIrmgLucsGdsGjFqdLqfLicL+fJZRlwrNilocrznJc25o2lWurLa0jRf92s7inWl+sScX" +
    "mtGgJbROS+GyvT4K6QVqUMUtGfPSw6yoih5rsqpre7NLMa50o/RijwRiZQGtu54l68/AvAWUeQ3z1pIHc0oQklJYoss0QQA9" +
    "xiFsOA2Bf4CByBDFFrUOuPWjxjwvw9cFlW9wpmNu9iwP0vp/ImPvp49uiFdfoBa0rgohGNDOx0qpGZUDGxDV6jNRonrCwJ4V" +
    "0FFKZVUQIpAC1550XNVIysUAW26twyvajaSf6tEdj2Kk4vm3YVLT5fj/8VBAHr/8AS43rRTWlqG8n57/yzfs3nD1+spvuR57" +
    "iEA/YAOyuYbmyjYWG8O7tb3UutUHqPY21fCc/ppszV8Bg8BU6hU6hNmTZjt07dJUyVkxbRtilf7unLPh+Wa3oFzuGPxmPsFj" +
    "sEfGR8ZHxke0btG7TLPLPLPLOFhYWFhYWFhYWFhYWFhYWFhYWFhcs8s8s8s8s8s4WFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhY" +
    "WFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYXg==";

  /// <summary>The exact PCM handed to ffmpeg, regenerated rather than stored.</summary>
  public static short[] Source() {
    const int frames = 12 * 1_024;
    var pcm = new short[frames];
    for (var i = 0; i < frames; ++i) {
      var t = (double)i / SampleRate;
      var value = 7_000 * Math.Sin(2 * Math.PI * 440 * t) + 1_500 * Math.Sin(2 * Math.PI * 1_970 * t);
      if (i is >= 5_000 and < 5_400)
        value += 18_000 * Math.Sin(2 * Math.PI * 3_100 * (i - 5_000) / SampleRate) * Math.Exp(-(i - 5_000) / 70.0);
      pcm[i] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
    }
    return pcm;
  }

  /// <summary>The stream's access units with their ADTS transport headers removed.</summary>
  public static byte[] RawAccessUnits() {
    var adts = Convert.FromBase64String(AdtsBase64);
    using var raw = new MemoryStream();
    for (var offset = 0; offset < adts.Length;) {
      var header = AacAdtsReader.ParseHeader(adts, offset);
      raw.Write(adts, offset + header.HeaderLengthBytes, header.FrameLength - header.HeaderLengthBytes);
      offset += header.FrameLength;
    }
    return raw.ToArray();
  }

  /// <summary>
  /// Signal-to-noise ratio in dB of <paramref name="decoded"/> against <see cref="Source"/>,
  /// skipping the encoder delay, plus the ratio of their RMS levels.
  /// </summary>
  public static (double SignalToNoiseDb, double LevelRatio) Compare(ReadOnlySpan<short> decoded) {
    var source = Source();
    if (decoded.Length < EncoderDelay + source.Length)
      throw new InvalidOperationException(
        $"Decoded {decoded.Length} samples, need at least {EncoderDelay + source.Length}.");

    double squaredError = 0, squaredSource = 0, squaredDecoded = 0;
    for (var i = 0; i < source.Length; ++i) {
      double wanted = source[i], got = decoded[EncoderDelay + i];
      squaredError += (wanted - got) * (wanted - got);
      squaredSource += wanted * wanted;
      squaredDecoded += got * got;
    }
    return (10 * Math.Log10(squaredSource / Math.Max(double.Epsilon, squaredError)),
      Math.Sqrt(squaredDecoded / squaredSource));
  }
}

/// <summary>
/// AAC-LC decode and encode pinned against ffmpeg.
/// </summary>
/// <remarks>
/// Nothing outside this repository could read what the AAC encoder wrote, and the decoder
/// turned every foreign stream into full-scale noise. Three causes, all in the filter bank
/// and its scaling: the IMDCT cosine argument used <c>(2k+1)</c> where ISO/IEC 14496-3
/// §4.6.11.2.2 has <c>(k+1/2)</c> and its gain was <c>4/N</c> rather than <c>2/N</c>; the
/// PCM conversion multiplied the already int16-scaled filter-bank output by 32768 again;
/// and inverse TNS ignored the transmitted band range, filtering whole windows in the
/// wrong direction. Encoder and decoder shared the same wrong transform, so every
/// round-trip test passed while no third-party tool agreed with either of them.
/// </remarks>
[TestFixture]
public sealed class AacDecoderOracleTests {

  [Test]
  [Category("RoundTrip")]
  public void DecodesFfmpegAacLcAcrossAllWindowSequences() {
    var adts = Convert.FromBase64String(FfmpegAacVector.AdtsBase64);
    using var input = new MemoryStream(adts, writable: false);
    using var output = new MemoryStream();
    AacCodec.Decompress(input, output);

    var decoded = ToSamples(output.ToArray());
    var (signalToNoiseDb, levelRatio) = FfmpegAacVector.Compare(decoded);

    Assert.Multiple(() => {
      Assert.That(signalToNoiseDb, Is.GreaterThan(20.0),
        "decoding ffmpeg's AAC-LC must reconstruct the source as well as ffmpeg's own decoder does");
      Assert.That(levelRatio, Is.EqualTo(1.0).Within(0.05),
        "the decoded level must match the source level, not a power-of-two multiple of it");
    });
  }

  [Test]
  [Category("RoundTrip")]
  public void EncodeDecodeRoundTripKeepsSignalLevel() {
    var source = FfmpegAacVector.Source();
    var encoded = AacEncoder.Encode(source, new AacEncoderOptions(
      FfmpegAacVector.SampleRate, Channels: 1, Bitrate: 96_000));

    using var input = new MemoryStream(encoded, writable: false);
    using var output = new MemoryStream();
    AacCodec.Decompress(input, output);

    var decoded = ToSamples(output.ToArray());
    var (signalToNoiseDb, levelRatio) = FfmpegAacVector.Compare(decoded);

    Assert.Multiple(() => {
      Assert.That(signalToNoiseDb, Is.GreaterThan(30.0));
      Assert.That(levelRatio, Is.EqualTo(1.0).Within(0.03),
        "encoder and decoder must agree on the int16 sample scale");
    });
  }

  private static short[] ToSamples(byte[] pcm) {
    var samples = new short[pcm.Length / sizeof(short)];
    Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * sizeof(short));
    return samples;
  }
}
