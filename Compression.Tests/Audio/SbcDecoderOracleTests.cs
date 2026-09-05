using System.Security.Cryptography;
using Codec.Sbc;
using NUnit.Framework;

namespace Compression.Tests.Audio;

/// <summary>
/// SBC decode pinned against libavcodec's own output.
/// </summary>
/// <remarks>
/// The synthesis filterbank summed its matrix products into a <c>uint</c> and shifted
/// that right by 15. The sum is signed, so every negative sample zero-filled its top
/// bits and came back as a large positive one: framing, frame count and sample count
/// were all correct, the first samples matched, and everything after the signal first
/// went negative was noise — 9095 zero crossings a second where 880 belong.
///
/// The stream below is 0.08 s of a 440 Hz sine at 16 kHz, encoded by ffmpeg 9.0.1;
/// the digest is of the PCM ffmpeg itself decodes from it.
/// </remarks>
[TestFixture]
public sealed class SbcDecoderOracleTests {

  private const string EncodedSbc =
    "nDE8ybdlRUSAMDAv3r5++BDzBPYD96+FsfHxHTt7CLstHqK0NXK0d9+4hp5pruYjTWSL2+tiUjGP3L188A73CQXT+E9tD5Aw" +
    "H758/4j/AP37+A/AD+/v379+8fT+/wH79+8bj+/v379++67/AP379+/6b+/v379+9yz+/v379++cMTwIsAAAAACEftpY3Inl" +
    "1WN1IWoH4vJ6XzF2onpM2Rv0IJYHXiqUluIpXWoBJrQOGn3V4qxYTyHnYYXP4ytzVWGYm4LxwQWmHG5QLifNJ431Zk4tTPH4" +
    "3n3OpnezG3Yn1tm2xPYnH2ojcxruYnGGZgsAQq3pZG4Jz1W0HWnZZJwxPAiwAAAA/9ePAgkejckvW86ieihkB0KEmgjaWhP8" +
    "k/Migbeh4KxXCWdhee5HC9NgecffRPS8vdiicnBW9s8ngfcnjRxXChfkga/3p/IZbicgoqjGqdkefcDYSeynkcgaMm0+ueZe" +
    "hg1WpnGcZhke7ZO9GkfhncU6au2ghddinDE8CLAAAAABzEbGmaVyN9DTz2SaKNn9HIEpdt+lix0bEJ9uB+IVl4LKOFx+gmSD" +
    "kFyJ+RzIzU8VyKaVj0/rtOBlqN5jCaK6GSFloAJ2755qBuJtCUERyBx1r4vHlh5uSh3ANb7Jx6OB0fUqbyiF2CAbjkDF6CJ9" +
    "+7CkE+FxyJecMTwIsAAAAPqPkQl3n4l3LipvHX2npACEftpY3Inl1WN1IWoH4vJ6XzF2onpM2Rv0IJYHXiqUluIpXWoBJrQO" +
    "Gn3V4qxYTyHnYYXP4ytzVWGYm4LxwQWmHG5QLifNJ431Zk4tTPH43n3OpnezG3Yn1tm2xPYnH2ojcxruYnGGZpwxPAiwAAAA" +
    "CwBCrelkbgnPVbQdadlk/9ePAgkejckvW86ieihkB0KEmgjaWhP8k/Migbeh4KxXCWdhee5HC9NgecffRPS8vdiicnBW9s8n" +
    "gfcnjRxXChfkga/3p/IZbicgoqjGqdkefcDYSeynkcgaMm0+ueZehg1WpnGcZhkenDE8CLAAAADtk70aR+GdxTpq7aCF12IB" +
    "zEbGmaVyN9DTz2SaKNn9HIEpdt+lix0bEJ9uB+IVl4LKOFx+gmSDkFyJ+RzIzU8VyKaVj0/rtOBlqN5jCaK6GSFloAJ2755q" +
    "BuJtCUERyBx1r4vHlh5uSh3ANb7Jx6OB0fUqbyiF2CCcMTwIsAAAABuOQMXoIn37sKQT4XHIl/qPkQl3n4l3LipvHX2npACE" +
    "ftpY3Inl1WN1IWoH4vJ6XzF2onpM2Rv0IJYHXiqUluIpXWoBJrQOGn3V4qxYTyHnYYXP4ytzVWGYm4LxwQWmHG5QLifNJ431" +
    "Zk4tTPH43n3OpnezG3Yn1pwxPAiwAAAA2bbE9icfaiNzGu5icYZmCwBCrelkbgnPVbQdadlk/9ePAgkejckvW86ieihkB0KE" +
    "mgjaWhP8k/Migbeh4KxXCWdhee5HC9NgecffRPS8vdiicnBW9s8ngfcnjRxXChfkga/3p/IZbicgoqjGqdkefcDYSeynkcga" +
    "nDE8CLAAAAAybT655l6GDVamcZxmGR7tk70aR+GdxTpq7aCF12IBzEbGmaVyN9DTz2SaKNn9HIEpdt+lix0bEJ9uB+IVl4LK" +
    "OFx+gmSDkFyJ+RzIzU8VyKaVj0/rtOBlqN5jCaK6GSFloAJ2755qBuJtCUERyBx1r4vHlh5uSh0=";

  /// <summary>SHA-256 of the interleaved 16-bit PCM libavcodec produces from that stream.</summary>
  private const string FfmpegPcmDigest =
    "484d2ce51be7a875290da8580ac6fe5ef45feb0a39fd1f8db509dbddc05cf520";

  [Test]
  [Category("RoundTrip")]
  public void DecodeMatchesLibavcodecByteForByte() {
    var encoded = Convert.FromBase64String(EncodedSbc);
    var channels = SbcCodec.DecodeToChannels(encoded, out var sampleRate, out var channelCount);

    Assert.Multiple(() => {
      Assert.That(sampleRate, Is.EqualTo(16_000));
      Assert.That(channelCount, Is.EqualTo(1));
    });

    var pcm = new byte[channels[0].Length * channelCount * 2];
    for (var i = 0; i < channels[0].Length; ++i)
      for (var c = 0; c < channelCount; ++c) {
        var sample = channels[c][i];
        var offset = (i * channelCount + c) * 2;
        pcm[offset] = (byte)(sample & 0xFF);
        pcm[offset + 1] = (byte)((sample >> 8) & 0xFF);
      }

    Assert.That(Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(),
      Is.EqualTo(FfmpegPcmDigest));
  }

  /// <summary>
  /// The symptom the digest would not explain on its own: a 440 Hz tone crosses zero
  /// about 880 times a second, and the broken shift turned that into thousands.
  /// </summary>
  [Test]
  public void DecodedToneKeepsItsFundamental() {
    var channels = SbcCodec.DecodeToChannels(
      Convert.FromBase64String(EncodedSbc), out var sampleRate, out _);

    // Past the synthesis filterbank's priming: it starts from a zeroed history, and
    // over a clip this short that ramp would otherwise dominate the count.
    const int primed = 256;
    var samples = channels[0];
    var crossings = 0;
    for (var i = primed + 1; i < samples.Length; ++i)
      if ((samples[i - 1] < 0) != (samples[i] < 0))
        ++crossings;

    var perSecond = crossings * (double)sampleRate / (samples.Length - primed);
    Assert.That(perSecond, Is.EqualTo(880).Within(80), "a 440 Hz tone crosses zero ~880 times a second");
  }
}
