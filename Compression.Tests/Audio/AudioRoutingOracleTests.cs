using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

/// <summary>
/// Formats whose codecs work but that the conversion graph could not reach, and
/// formats whose codecs do not work on anyone else's streams.
/// </summary>
/// <remarks>
/// <para>
/// QOA and Opus each had a complete codec and no route: converting either into
/// anything failed with "no audio conversion route exists" while the decoder sat
/// one call away. Opus additionally emitted the encoder's trailing padding as
/// audio, because nothing read the final page's granule position.
/// </para>
/// <para>
/// AC-3 and DTS are the other case. Both decoders read what our own encoders
/// write and give up on the first frame of anything else, and the pipeline built
/// a valid, entirely empty file out of the nothing they returned. An empty file
/// that claims to be a conversion cannot be told apart from silence that was
/// really in the source, so the adapters now refuse instead.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AudioRoutingOracleTests {

  private static IFormatDescriptor Descriptor(string id) {
    FormatRegistration.EnsureInitialized();
    return FormatRegistry.All.Single(descriptor => descriptor.Id == id);
  }

  private static byte[] ConvertToWav(byte[] input, string sourceId) {
    using var source = new MemoryStream(input, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      source, Descriptor(sourceId), output, new WavFormatDescriptor(),
      new FormatCreateOptions(Method: "pcm"));
    return output.ToArray();
  }

  private static byte[] WavPayload(byte[] wav, out int channels, out int sampleRate) {
    var position = 12;
    channels = 0;
    sampleRate = 0;
    while (position + 8 <= wav.Length) {
      var id = System.Text.Encoding.ASCII.GetString(wav, position, 4);
      var size = BitConverter.ToInt32(wav, position + 4);
      if (id == "fmt ") {
        channels = BitConverter.ToInt16(wav, position + 10);
        sampleRate = BitConverter.ToInt32(wav, position + 12);
      } else if (id == "data")
        return wav[(position + 8)..Math.Min(wav.Length, position + 8 + size)];

      position += 8 + size + (size & 1);
    }

    return [];
  }

  // ── QOA ───────────────────────────────────────────────────────────────────

  /// <summary>0.05 s of a 440 Hz sine, 44.1 kHz stereo.</summary>
  private const string QoaStream =
    "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" +
    "CBCFRJCBJBADHiAAAAAAAAMeIAAAAAAA";

  /// <summary>SHA-256 of the interleaved 16-bit PCM libavcodec decodes from it.</summary>
  private const string QoaPcmDigest =
    "80f139f464281e408daa533db0cc3126e22f3df91a7716e5acee9ebf875932c4";

  [Test]
  [Category("RoundTrip")]
  public void QoaConvertsAndMatchesLibavcodecByteForByte() {
    var wav = ConvertToWav(System.Convert.FromBase64String(QoaStream), "Qoa");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(sampleRate, Is.EqualTo(44_100));
      Assert.That(System.Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), Is.EqualTo(QoaPcmDigest),
        "decoded PCM must be byte-for-byte what libavcodec decodes from the same stream");
    });
  }

  // ── Opus ──────────────────────────────────────────────────────────────────

  /// <summary>0.12 s of a 440 Hz sine at 48 kHz mono, encoded by libopus.</summary>
  private const string OpusStream =
    "T2dnUwACAAAAAAAAAADFjHQOAAAAAEB0zF4BE09wdXNIZWFkAQE4AYC7AAAAAABPZ2dTAAAAAAAAAAAAAMWMdA4BAAAALspvpQE8" +
    "T3B1c1RhZ3MMAAAATGF2ZjYzLjEuMTAxAQAAABwAAABlbmNvZGVyPUxhdmM2My4xLjEwMSBsaWJvcHVzT2dnUwAEuBcAAAAAAADF" +
    "jHQOAgAAANW7eF0HX0I7ODs+MXiBp11snpmsAAAICuBa1RGcRDEVBXs95n4cuPkNWVlfIdC6OqWOdTg8Ghgfufrxatn11XCRTws4" +
    "Gv6YQ7C1Q6rwWWvi9DUODQBlxA0S4eFybEGNMpE4sshIlIDlpeoEeJ9nAef8lU1OqhinGd/lXFnf+26hM2X1qKVJDeRXd4ePCkLQ" +
    "kkaR/R/UO3A/99WXqDhWglIlcAu6ljwcD09B94m9eJqy33Wc/Es6RX4oWnKHxoOIOxxHKhYoGBNEAf+6D5PCseF12KP8lNFsToFx" +
    "tvMgI6ef9dbn9yzdfAV4mrLfdZz8Re3XM58TY98QE9kP78C3oXi8J2i5ib9or8iK8NkHMqPIoenVHibH38UOrXdvWUsTTHiast91" +
    "nPxJM79K9yUjaKaNIAF1dpvtO+elJaoSBD/u6+9GTJaJIFhYf2f3X7843vxWl/gpp2v2SeUEeJqN4JK/u7tAJR9gJPWj4OkQGATl" +
    "/RETJ63/cBcOlcCXbrZ9j8r2Q7PokiS6TdsbMQv5wkFLatUbCvK1FU14BlcYh/MnKVo0qLsKD9+vzu8GozyNUOjik8rMz7Zzszys" +
    "1oWjenlX0KakRJke4GaO";

  /// <summary>
  /// libopus pads the last packet out to a whole frame, and the granule position
  /// on the final page is what says where the audio really stops. Decoding every
  /// packet and stopping there hands back the padding as if it were signal.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void OpusStopsWhereTheFinalGranuleSaysItDoes() {
    var wav = ConvertToWav(System.Convert.FromBase64String(OpusStream), "Opus");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(1));
      // Opus always decodes at 48 kHz whatever the input rate was.
      Assert.That(sampleRate, Is.EqualTo(48_000));
      // libavcodec reports 5760 frames for this stream; the untrimmed decode ran on.
      Assert.That(pcm.Length / 2, Is.EqualTo(5_760), "frames after trimming");
    });

    // and what came back is the tone, not silence
    var peak = 0;
    for (var i = 0; i + 1 < pcm.Length; i += 2)
      peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(pcm, i)));
    Assert.That(peak, Is.GreaterThan(1_000), "decoded a silent buffer");
  }


  // ── TTA ───────────────────────────────────────────────────────────────────

  /// <summary>0.05 s of a 440 Hz sine, 44.1 kHz stereo, encoded by libavcodec.</summary>
  private const string TtaStream =
    "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" +
    "gAAAAAAAAAAA";

  /// <summary>SHA-256 of the interleaved 16-bit PCM libavcodec decodes from it.</summary>
  private const string TtaPcmDigest =
    "3b354e0522ec8d3ba9f9ef7cebb2238b3afe66468d2f0d59dd7b9c9c4d7a295b";

  /// <summary>
  /// TTA codes its unary prefix as ones terminated by a zero. The reader counted
  /// the opposite — zeros terminated by a one — and the writer emitted the same
  /// inversion, so the pair round-tripped perfectly and neither could exchange a
  /// file with anything else. The frame CRC passes either way, since it covers
  /// the bytes and not what they mean, which is why nothing caught it.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void TtaConvertsAndMatchesLibavcodecByteForByte() {
    var wav = ConvertToWav(System.Convert.FromBase64String(TtaStream), "Tta");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(sampleRate, Is.EqualTo(44_100));
      Assert.That(System.Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), Is.EqualTo(TtaPcmDigest),
        "decoded PCM must be byte-for-byte what libavcodec decodes from the same stream");
    });
  }

  /// <summary>What we write must come back through our own reader unchanged.</summary>
  [TestCase(1, 44_100, 16)]
  [TestCase(2, 48_000, 24)]
  [TestCase(2, 44_100, 8)]
  [Category("RoundTrip")]
  public void TtaRoundTripsEveryWidthItAccepts(int channels, int sampleRate, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    var frames = sampleRate / 10;
    var pcm = new byte[frames * channels * bytesPerSample];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < channels; ++c) {
        var value = (int)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * ((1L << (bitsPerSample - 1)) - 1) * 0.8);
        var offset = (i * channels + c) * bytesPerSample;
        if (bytesPerSample == 1)
          pcm[offset] = (byte)(value + 128);
        else
          for (var b = 0; b < bytesPerSample; ++b)
            pcm[offset + b] = (byte)(value >> (b * 8));
      }

    using var input = new MemoryStream(pcm, writable: false);
    using var encoded = new MemoryStream();
    Codec.Tta.TtaCodec.Compress(input, encoded, channels, sampleRate, bitsPerSample);

    encoded.Position = 0;
    using var decoded = new MemoryStream();
    Codec.Tta.TtaCodec.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(pcm));
  }

  // ── AC-3 and DTS ──────────────────────────────────────────────────────────

  /// <summary>0.07 s of a 440 Hz sine, 48 kHz stereo, encoded by libavcodec.</summary>
  private const string Ac3Stream =
    "C3c4kAxAQ+EG8AEMDAwhgYGH/Z++huO9wxx3z4Zf2cmTJAEqDuq6Hx5BZXWPyZD9U+qBdCVeZHnhFkb5tylMqrEXYwOaxz1BqfJW" +
    "9ef7CPRmXDpTZT0bUqRdrBROuHKG7Ln0RasbFcSpu9bj6NyX5l9oilXGyWlQd1XQ+PILK6x+TIfqn1QLoSrzI88AAAAAAAAAAAAA" +
    "AAAAAAAAAePGpMGtOktejKf58h2FL97ChVyjKf58+Pnz58+fPgAqRkE60HkVLvyAMxMYY20IvX2nKUKkZBOtB5FS78gDMTGMGFNc" +
    "0MFM5AqEOXChFGdvHF6eQ9FyBqcRwyw2mcgVCHLhQijO3ji9OkwYTcdkQKTZxQg3BFcsjYDSgV6jgrtOEypJSbOKEG4IrlkbAaUC" +
    "ukwYVNzQwflHv74Q0ftu9y/1DvxiW5VquQ5aLM/KPf3who/bd7l/qHfmDCbDpUCyaj2h2uVXBlBvnUnVUdi7QlNAMukGyaj2h2uV" +
    "XBlBvnUnUNbiC3d+cAxAQ+EG8AEMDAwhgYGH1p7TfUZWY/kOQqS0n6WGUZWY/nx8+fPnz58Zf3fnz5AFydQi1Rda0xDpRSOySloj" +
    "xKaxjW23VTloZc0ss2sIss1cnUItUXWtMQ6UUjskpaI8MjBhTpy/ByREp5FZAaeH1gTPb4xcCtwWiM3Vj0OCTryhvEn71RjH9L+j" +
    "yREp5FZAaeH1gTPb4xcCtzBhNxzWBhfHoxPLLIsAtaBuzV8XhF7ZLGlKesLiWSZzHeQglDgh8RhfHoxPLLIsAtaBuzV8XhF5Zgwq" +
    "bShgYV93r4h9+DNyhAA+AOm2DDxU63oA7VkzpTWblBJ9RRWCKymFfd6+IffgzcoQAPgDptgwYMJsKyIM4nqOdFHMFlMMdl37MXHW" +
    "ueucTSI5vlPO7nJRHuE+pmIXZxPUc6KOYLKYY7Lv2YuOtcwYU5zqga9vltlhwm3WUHk/zduunUfiRNhKIcaWKxUhrJYcE9jZJb17" +
    "fLbLDhNusoPJ/m7ddOo4AM7vC3dTzwxAQ+EG8AEMDAwhgYGH/m4mVMY+fBjHz4Zf2IkSJAAYAAbC7AZllNzaHp9IDZ/gAv/gAAAA" +
    "AAAAAAAAAAAbbxrYPY0GAAGwuwGZZTc2h6fSA2f4AL/4AAAAAAAAAAAAAAAG2wwfpXfvnJw4Y5P58Ab2/zntepH1pbgMNhxc0Rb8" +
    "pSx/OoXf4cSRpQyrC3i1SFDS3T2HIz8UViITUIVNeLLphmVfkjoXdZ65WjhmJkB6ER7cLq0Y88bhILaJ0tQLsxvjb2/zntepH1pb" +
    "gMNhxc0Rb8pSx/AAAAAAAAAAAAAAAAO7gfMjB+8N8+5VDfHKvnoAre/0TZrPgLFISLNAC3j6ah0hNSkMymngVt/es44kgK6YH3hZ" +
    "jJ87bm5xWuFoHy9Aakse1WXWq64EYW2XJ2xR3Cre/0TZrPgLFISLNAAAAAAAAAG22pMhAQEEICAglWPnz58+ff86vnz58f86vnz5" +
    "8AA3eNoG7SYAAAbvG0DdpMAAAN3jaBu0gJ7U";

  /// <summary>
  /// A stream whose header declares audio but which the decoder cannot read has
  /// to come back as a refusal, not as a well-formed empty file.
  /// </summary>
  /// <remarks>
  /// The pipeline will build a target out of whatever PCM it is handed, so a
  /// decoder that quietly returns nothing yields a valid WAV with a correct
  /// header and no samples — indistinguishable from silence that was really in
  /// the source.
  /// </remarks>
  [Test]
  public void Ac3ThatTheDecoderCannotReadRefusesRatherThanEmitAnEmptyFile() {
    var stream = System.Convert.FromBase64String(Ac3Stream);

    var thrown = Assert.Catch(() => ConvertToWav(stream, "Ac3"));
    Assert.That(thrown, Is.Not.Null, "an unreadable stream must not convert silently");
    Assert.That(thrown!.Message, Does.Contain("no samples").IgnoreCase.Or.Contain("no audio conversion route"),
      $"unexpected failure: {thrown.Message}");
  }
}
