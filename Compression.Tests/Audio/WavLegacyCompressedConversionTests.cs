using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class WavLegacyCompressedConversionTests {
  [TestCase("oki-adpcm", 0x0010, 8_000)]
  [TestCase("dialogic-oki-adpcm", 0x0017, 8_000)]
  [TestCase("gsm610", 0x0031, 8_000)]
  [TestCase("g721", 0x0040, 8_000)]
  [TestCase("g726-16", 0x0045, 8_000)]
  [TestCase("g726-24", 0x0045, 8_000)]
  [TestCase("g726-32", 0x0045, 8_000)]
  [TestCase("g726-40", 0x0045, 8_000)]
  [TestCase("g726-apicom-16", 0x0064, 8_000)]
  [TestCase("g726-apicom-24", 0x0064, 8_000)]
  [TestCase("g726-apicom-32", 0x0064, 8_000)]
  [TestCase("g726-apicom-40", 0x0064, 8_000)]
  [TestCase("g722", 0x028F, 16_000)]
  [TestCase("g722-apicom", 0x0065, 16_000)]
  public void WavWriter_LegacyCodec_RoundTripsThroughCanonicalPcm(string codec, int formatTag, int sampleRate) {
    const int frames = 643;
    var pcm = BuildPcm16(sampleRate, frames);
    var source = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);

    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var descriptor = new WavFormatDescriptor();
    AudioConversionOperation.Convert(input, descriptor, output, descriptor, new FormatCreateOptions(Method: codec));

    var encoded = output.ToArray();
    var decoded = new WavReader().ReadCanonicalPcm(encoded);

    Assert.Multiple(() => {
      Assert.That(ReadFormatTag(encoded), Is.EqualTo(formatTag));
      Assert.That(ReadFactSampleFrames(encoded), Is.EqualTo((uint)frames));
      Assert.That(decoded.FormatCode, Is.EqualTo(1));
      Assert.That(decoded.NumChannels, Is.EqualTo(1));
      Assert.That(decoded.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedPcm.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedPcm.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void Gsm610Writer_UsesMicrosoftWav49Geometry() {
    var pcm = BuildPcm16(8_000, 337);
    var source = PcmCodec.ToWavBlob(pcm, 1, 8_000, 16);
    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var descriptor = new WavFormatDescriptor();

    AudioConversionOperation.Convert(input, descriptor, output, descriptor, new FormatCreateOptions(Method: "gsm610"));
    var wav = output.ToArray();
    var fmt = FindChunk(wav, "fmt "u8) + 8;
    var data = FindChunk(wav, "data"u8);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt)), Is.EqualTo(0x0031));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 12)), Is.EqualTo(65));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 16)), Is.EqualTo(2));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 18)), Is.EqualTo(320));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(data + 4)) % 65, Is.Zero);
    });
  }

  /// <summary>
  /// Known-answer decode of a 32 kbit/s G.726 frame against the ITU-T G.191 Software Tools
  /// Library reference decoder (module <c>G726</c>, <c>G726_decode</c> at <c>rate = 4</c>),
  /// sampled at the linear reconstructed signal SR before the output PCM format conversion
  /// and synchronous coding adjustment of G.726 § 4.2.8. The same answer is produced by
  /// spandsp's independent G.726 decoder.
  /// </summary>
  [Test]
  public void G726Reader_DecodesItuReferenceKnownAnswerFrame() {
    var data = Convert.FromHexString("F77777777F8A9ABCDF245555321DBAAA");
    var wav = BuildCompressedWave(0x0045, 8_000, 4, 1, 4_000, data, 32);
    var parsed = new WavReader().ReadCanonicalPcm(wav);
    var expected = new short[] {
      0, 88, 120, 172, 244, 376, 584, 1052, 2116, 440, -2744, -7232, -11368, -13116, -12192, -10632,
      -8268, -4436, -488, 3640, 6988, 9148, 10884, 12300, 10224, 8032, 5656, 1392, -2868, -6512, -8984, -11052,
    };

    Assert.That(ReadPcm16(parsed.InterleavedPcm), Is.EqualTo(expected));
  }

  [Test]
  public void G722Reader_DecodesFfmpegKnownAnswerFrame() {
    var data = Convert.FromHexString("FA96298C2190A0A060E0E0E7ECF4DF55");
    var wav = BuildCompressedWave(0x028F, 16_000, 4, 1, 8_000, data, 32);
    var parsed = new WavReader().ReadCanonicalPcm(wav);
    var expected = new short[] {
      0, -1, -1, 0, 0, -1, -1, 0, 0, -2, 0, 6, -2, -14, -4, 24,
      9, -50, -14, 55, 58, -78, -68, 182, 330, 341, 764, 1584, 2048, 2992, 6498, 11278,
    };

    Assert.That(ReadPcm16(parsed.InterleavedPcm), Is.EqualTo(expected));
  }

  private static byte[] BuildPcm16(int sampleRate, int frames) {
    var pcm = new byte[frames * 2];
    for (var frame = 0; frame < frames; ++frame) {
      var sample = (short)Math.Round((
        Math.Sin(2 * Math.PI * 310 * frame / sampleRate) * 10_000 +
        Math.Sin(2 * Math.PI * 730 * frame / sampleRate) * 2_000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 2, 2), sample);
    }
    return pcm;
  }

  private static byte[] BuildCompressedWave(ushort tag, int sampleRate, ushort bitsPerSample,
    ushort blockAlign, uint averageBytesPerSecond, byte[] data, uint factFrames) {
    var result = new byte[12 + 8 + 18 + 12 + 8 + data.Length + (data.Length & 1)];
    "RIFF"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
    "WAVE"u8.CopyTo(result.AsSpan(8));
    var pos = 12;
    "fmt "u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), 18);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos + 8), tag);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos + 10), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 12), checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 16), averageBytesPerSecond);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos + 20), blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos + 22), bitsPerSample);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos + 24), 0);
    pos += 26;
    "fact"u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 8), factFrames);
    pos += 12;
    "data"u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), checked((uint)data.Length));
    data.CopyTo(result.AsSpan(pos + 8));
    return result;
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

  private static short[] ReadPcm16(ReadOnlySpan<byte> bytes) {
    var samples = new short[bytes.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
    return samples;
  }
}
