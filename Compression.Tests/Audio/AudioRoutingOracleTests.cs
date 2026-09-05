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
