using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aiff;
using FileFormat.Au;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class G711PacketRemuxTests {

  [TestCase("alaw", 27u, "alaw")]
  [TestCase("mulaw", 1u, "ulaw")]
  public void WavToAuAndAifc_PreservesEncodedG711Bytes(string codec, uint auEncoding, string aifcCompression) {
    const int sampleRate = 8_000;
    const int channels = 2;
    const int frames = 1_603;
    var sourcePcm = PcmCodec.ToWavBlob(BuildPcm16(sampleRate, channels, frames), channels, sampleRate, 16);

    using var pcmInput = new MemoryStream(sourcePcm, writable: false);
    using var compressedWav = new MemoryStream();
    AudioConversionOperation.Convert(
      pcmInput,
      new WavFormatDescriptor(),
      compressedWav,
      new WavFormatDescriptor(),
      new FormatCreateOptions(Method: codec));

    var wav = new WavReader().Read(compressedWav.ToArray());
    var encodedPayload = wav.InterleavedPcm;

    using var wavForAu = new MemoryStream(compressedWav.ToArray(), writable: false);
    using var auOutput = new MemoryStream();
    AudioConversionOperation.Convert(wavForAu, new WavFormatDescriptor(), auOutput, new AuFormatDescriptor());
    var au = new AuReader().Read(auOutput.ToArray());

    using var wavForAifc = new MemoryStream(compressedWav.ToArray(), writable: false);
    using var aifcOutput = new MemoryStream();
    AudioConversionOperation.Convert(wavForAifc, new WavFormatDescriptor(), aifcOutput, new AiffFormatDescriptor());
    var aifc = new AiffReader().Read(aifcOutput.ToArray());

    Assert.Multiple(() => {
      Assert.That(au.Encoding, Is.EqualTo(auEncoding));
      Assert.That(au.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(au.NumChannels, Is.EqualTo(channels));
      Assert.That(au.SoundData, Is.EqualTo(encodedPayload));

      Assert.That(aifc.IsAifc, Is.True);
      Assert.That(aifc.CompressionId, Is.EqualTo(aifcCompression));
      Assert.That(aifc.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(aifc.NumChannels, Is.EqualTo(channels));
      Assert.That(aifc.SampleFrames, Is.EqualTo(frames));
      Assert.That(aifc.SoundData, Is.EqualTo(encodedPayload));
    });
  }

  [Test]
  public void AuToWav_PreservesMuLawPayloadWithoutPcmDecode() {
    const int sampleRate = 8_000;
    const int channels = 1;
    const int frames = 257;
    var payload = Enumerable.Range(0, frames).Select(static value => unchecked((byte)(value * 37))).ToArray();
    var au = BuildAu(payload, sampleRate, channels, encoding: 1);

    using var input = new MemoryStream(au, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(input, new AuFormatDescriptor(), output, new WavFormatDescriptor());

    var wav = new WavReader().Read(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(wav.FormatCode, Is.EqualTo(0x0007));
      Assert.That(wav.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(wav.NumChannels, Is.EqualTo(channels));
      Assert.That(wav.InterleavedPcm, Is.EqualTo(payload));
    });
  }

  private static byte[] BuildAu(byte[] payload, int sampleRate, int channels, uint encoding) {
    var result = new byte[24 + payload.Length];
    ".snd"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 24);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), checked((uint)payload.Length));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), encoding);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), checked((uint)channels));
    payload.CopyTo(result, 24);
    return result;
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)Math.Round(Math.Sin(2.0 * Math.PI * (300.0 + channel * 170.0) * frame / sampleRate) * 12_000.0);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2, 2), sample);
      }
    return pcm;
  }
}
