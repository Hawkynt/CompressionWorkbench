using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class WavCompressedConversionTests {

  [TestCase("alaw", 0x0006)]
  [TestCase("mulaw", 0x0007)]
  [TestCase("ima-adpcm", 0x0011)]
  [TestCase("ms-adpcm", 0x0002)]
  public void WavToCompressedWav_WritesRealFormatAndDecodesToOriginalFrameCount(string codec, int formatTag) {
    const int sampleRate = 8_000;
    const int channels = 1;
    const int frames = 1_603; // deliberately not aligned to ADPCM blocks
    var pcm = BuildPcm16(sampleRate, channels, frames);
    var source = PcmCodec.ToWavBlob(pcm, channels, sampleRate, 16);

    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var options = new FormatCreateOptions(Method: codec);
    if (codec is "ima-adpcm" or "ms-adpcm") options.FormatSpecific["block-align"] = "256";

    var descriptor = new WavFormatDescriptor();
    AudioConversionOperation.Convert(input, descriptor, output, descriptor, options);

    var encoded = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadFormatTag(encoded), Is.EqualTo(formatTag));
      Assert.That(ReadFactSampleFrames(encoded), Is.EqualTo((uint)frames));
    });

    var decoded = new WavReader().ReadCanonicalPcm(encoded);
    Assert.Multiple(() => {
      Assert.That(decoded.FormatCode, Is.EqualTo(1));
      Assert.That(decoded.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.NumChannels, Is.EqualTo(channels));
      Assert.That(decoded.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedPcm.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedPcm.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void SameWavWithoutCodecRequest_RemainsByteExact() {
    var pcm = BuildPcm16(44_100, 2, 1_024);
    var source = PcmCodec.ToWavBlob(pcm, 2, 44_100, 16);
    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var descriptor = new WavFormatDescriptor();

    AudioConversionOperation.Convert(input, descriptor, output, descriptor);

    Assert.That(output.ToArray(), Is.EqualTo(source));
  }

  private static ushort ReadFormatTag(ReadOnlySpan<byte> wav) {
    var offset = FindChunk(wav, "fmt "u8);
    return BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(offset + 8, 2));
  }

  private static uint ReadFactSampleFrames(ReadOnlySpan<byte> wav) {
    var offset = FindChunk(wav, "fact"u8);
    return BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 8, 4));
  }

  private static int FindChunk(ReadOnlySpan<byte> wav, ReadOnlySpan<byte> id) {
    for (var offset = 12; offset + 8 <= wav.Length;) {
      if (wav.Slice(offset, 4).SequenceEqual(id)) return offset;
      var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4)));
      offset += 8 + size + (size & 1);
    }
    throw new InvalidDataException($"WAVE chunk '{System.Text.Encoding.ASCII.GetString(id)}' not found.");
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
