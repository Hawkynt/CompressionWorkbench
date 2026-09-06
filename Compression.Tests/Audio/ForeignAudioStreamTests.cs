using System.Security.Cryptography;
using Codec.CriAdx;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

/// <summary>
/// Codecs checked against streams libavcodec produced rather than against
/// themselves.
/// </summary>
/// <remarks>
/// ADX is the fourth codec found writing and reading its own private variant of
/// a published format: the header's copyright offset is a 16-bit field of its
/// own at byte 2, and both halves treated it as the low bits of the magic word,
/// so the pair agreed with itself and put the sample data 32 bytes from where
/// every other tool looks for it.
/// </remarks>
[TestFixture]
public sealed class ForeignAudioStreamTests {

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

  // ── CRI ADX ───────────────────────────────────────────────────────────────

  /// <summary>0.08 s of a 440 Hz sine, 22.05 kHz stereo, encoded by libavcodec.</summary>
  private const string AdxStream =
    "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";

  /// <summary>SHA-256 of the PCM libavcodec decodes from it.</summary>
  private const string AdxPcmDigest = "9249b264b0af76ddf926f7cef47b3cfabd00ba600b9c8580f84ba9b53e277406";

  [Test]
  [Category("RoundTrip")]
  public void AdxDecodesLikeLibavcodec() {
    var wav = ConvertToWav(Convert.FromBase64String(AdxStream), "Adx");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(sampleRate, Is.EqualTo(22_050));
      Assert.That(Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), Is.EqualTo(AdxPcmDigest),
        "decoded PCM must be byte-for-byte what libavcodec decodes from the same stream");
    });
  }

  /// <summary>
  /// The magic occupies bytes 0-1 and the copyright offset is its own field at
  /// byte 2. Folding them into one word puts the sample data in the wrong place
  /// for every reader but our own.
  /// </summary>
  [Test]
  public void AdxHeaderKeepsTheCopyrightOffsetInItsOwnField() {
    var pcm = new short[4096];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * (i / 2) / 22_050.0) * 8_000);

    var encoded = AdxCodec.Encode(pcm, channels: 2, sampleRate: 22_050);

    Assert.Multiple(() => {
      Assert.That(encoded[0], Is.EqualTo(0x80), "magic high byte");
      Assert.That(encoded[1], Is.EqualTo(0x00), "magic low byte — not part of the offset");
      var copyrightOffset = (encoded[2] << 8) | encoded[3];
      Assert.That(copyrightOffset, Is.GreaterThanOrEqualTo(22), "offset must clear the fixed header fields");
      Assert.That(System.Text.Encoding.ASCII.GetString(encoded, copyrightOffset - 2, 6), Is.EqualTo("(c)CRI"));
    });

    // and it round-trips through our own reader
    var info = AdxCodec.ReadInfo(encoded);
    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(22_050));
  }

  // ── QuickTime IMA in AIFF ─────────────────────────────────────────────────

  /// <summary>The same audio as ima4 in AIFC, encoded by libavcodec.</summary>
  private const string Ima4Aiff =
    "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" +
    "iRhCNUVDQzM0MzMz/42AgICAgICAgAAIiIA=";

  /// <summary>
  /// AIFC's numSampleFrames can only honestly trim the padding inside the last
  /// packet. libavformat stores the packet count there, which for ima4 is a 64th
  /// of the frame count — taken at face value it threw away 63 frames in 64.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void Ima4InAiffDecodesEveryPacketNotJustTheHeaderCount() {
    var wav = ConvertToWav(Convert.FromBase64String(Ima4Aiff), "Aiff");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(sampleRate, Is.EqualTo(22_050));
      Assert.That(pcm.Length / 4, Is.EqualTo(1792), "frames decoded");
    });

    var peak = 0;
    for (var i = 0; i + 1 < pcm.Length; i += 2)
      peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(pcm, i)));
    Assert.That(peak, Is.GreaterThan(1_000), "decoded a silent buffer");
  }

  // ── RealAudio 14.4 (lpcJ) ─────────────────────────────────────────

  /// <summary>0.4 s of a 440 Hz sine, 8 kHz mono, encoded by libavcodec.</summary>
  private const string Ra144Stream =
    "LlJNRgAAABIAAAAAAAAAAAAFUFJPUAAAADIAAAAAH0AAAB9AAAAEAAAAABQAAAAVAAAAAAAAAAAAAAAAAAAA8QABAANDT05UAAAA" +
    "EgAAAAAAAAAAAABNRFBSAAAAmwAAAAAAAB9AAAAfQAAABAAAAAAUAAAAAAAAAAAAAAAAEFRoZSBBdWRpbyBTdHJlYW0UYXVkaW8v" +
    "eC1wbi1yZWFsYXVkaW8AAABJLnJh/QAEAAAucmE0AbU1MAAEAAAAOQADAAAAAAAFFUAAAOpgAADqYAABAAAAAAAAH0AAAAAQAAEE" +
    "SW50MARscGNKAAAAAAAAAERBVEEAAALEAAAAAAAVAAAAAAAAACAAAAAAAAAAAieUOFXGYAAAAAAAAAAAEAAAgO/+AAAAIAAAAAAA" +
    "AAACJ5Q4VcaperU6IbGlESUW48ix1IoAAAAgAAAAAAAAAAInlDhVxrIbp1ts3b8TZp/o5DX1YAAAACAAAAAAAAAAAieUOFXGshsz" +
    "PJB7aoyGtXJJPRSUAAAAIAAAAAAAAAACJ5Q4Vca2mv3bItJXFabiWa039TQAAAAgAAAAAAAAAAInlDhVxraa1sXY9yWNpuQ07TWW" +
    "EAAAACAAAAAAAAAAAieUOFXGtp9TOrR/PNyHgjftN5QSAAAAIAAAAAAAAAACJ5Q4Vca2mtEukNynBab5w2Q3H1QAAAAgAAAAAAAA" +
    "AAInlDhVxrabmnjY3BeFpqVc7TZtMgAAACAAAAAAAAAAAieUOFXGshrKqpDWkaSGv1IkNcmWAAAAIAAAAAAAAAACJ5Q4VcatmtKo" +
    "kNdc5IPaR+Q88o4AAAAgAAAAAAAAAAInlDhVxrIaf8KQ81obY+KuZDfdSAAAACAAAAAAAAAAAieUOFXGshrElNjWgbWm8aIkNbKU" +
    "AAAAIAAAAAAAAAACJ5Q4Vca2j44JtNwRBIPpdeQ3kJYAAAAgAAAAAAAAAAInlDhVxrIPuqKQ3RH8g+wZJDW58AAAACAAAAAAAAAA" +
    "AieUOFXGshuTKJDWak2mttRItITEAAAAIAAAAAAAAAACJ5Q4Vca2j3c9kN5i3aa1lm019fAAAAAgAAAAAAAAAAInlDhVxradkriQ" +
    "fe58h7QSbTWF3AAAACAAAAAAAAAAAieUOFXGsh7EKpDXMhyG9ZKkNeXwAAAAIAAAAAAAAAACJ5Q4VcayD5mwkN1JDabjkqQ3DUQA" +
    "AAAgAAAAAAAAAAIr8ChV1okboLu23xzhJ7bASV7GxAAAAAAAAAAA";

  /// <summary>SHA-256 of the PCM libavcodec decodes from it.</summary>
  private const string Ra144PcmDigest =
    "85e02381979dcd5a28cc6765d1ded1d3da43349166ba27aa30836515e7ad52c9";

  /// <summary>
  /// The gain of the adaptive codebook is the excitation's dominant term, and
  /// <c>add_wav</c> only evaluates it when the adaptive-codebook index is non-zero.
  /// Inverting that condition drops the term on every subblock that has one, which
  /// leaves the pitch structure audible but the amplitude at roughly a seventh.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void Ra144DecodesLikeLibavcodec() {
    var wav = ConvertToWav(Convert.FromBase64String(Ra144Stream), "RealMedia");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(1));
      Assert.That(sampleRate, Is.EqualTo(8_000));
      Assert.That(pcm.Length, Is.EqualTo(6720), "21 blocks of 160 samples");
      Assert.That(Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), Is.EqualTo(Ra144PcmDigest),
        "decoded PCM must be byte-for-byte what libavcodec decodes from the same stream");
    });
  }

  /// <summary>
  /// Amplitude is the symptom the sample-exact digest above would not name on its own:
  /// the pre-fix decoder still produced the right pitch, at an RMS seven times too low.
  /// </summary>
  [Test]
  public void Ra144ReachesTheAmplitudeLibavcodecDoes() {
    var wav = ConvertToWav(Convert.FromBase64String(Ra144Stream), "RealMedia");
    var pcm = WavPayload(wav, out _, out _);

    var peak = 0;
    double energy = 0;
    for (var i = 0; i + 1 < pcm.Length; i += 2) {
      var sample = BitConverter.ToInt16(pcm, i);
      peak = Math.Max(peak, Math.Abs((int)sample));
      energy += (double)sample * sample;
    }

    var rms = Math.Sqrt(energy / (pcm.Length / 2));
    Assert.Multiple(() => {
      Assert.That(peak, Is.EqualTo(4516), "peak libavcodec decodes");
      Assert.That(rms, Is.EqualTo(2817).Within(1.0), "RMS libavcodec decodes");
    });
  }
}
