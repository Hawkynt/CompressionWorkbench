#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

[TestFixture]
public class WavFloatSplitTests {

  // 48 kHz stereo IEEE-float WAV (format code 3), `frames` frames, 32 or 64 bit.
  private static byte[] MakeFloatStereoWav(int bits) {
    const int frames = 8;
    var bytesPerSample = bits / 8;
    var pcm = new byte[frames * 2 * bytesPerSample];
    for (var i = 0; i < frames; ++i) {
      // 32-bit branch stores single precision; 64-bit stores full double precision.
      // The assertions below read back with the matching precision.
      WriteFloat(pcm, (i * 2) * bytesPerSample, bits == 32 ? i * 0.1f : i * 0.1, bits);
      WriteFloat(pcm, (i * 2 + 1) * bytesPerSample, bits == 32 ? i * -0.2f : i * -0.2, bits);
    }
    return PcmCodec.ToWavBlob(pcm, channels: 2, sampleRate: 48000, bitsPerSample: bits, formatCode: 3);
  }

  private static void WriteFloat(byte[] buf, int offset, double value, int bits) {
    if (bits == 32)
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), (float)value);
    else
      BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(offset), value);
  }

  [Test]
  public void Float32_SplitsIntoFloatChannels() {
    var blob = MakeFloatStereoWav(32);
    using var ms = new MemoryStream(blob);
    var entries = new WavFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "RIGHT.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Float32_ExtractedChannelIsFloatWavWithExactSamples() {
    var blob = MakeFloatStereoWav(32);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new WavFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();

    // fmt format code (offset 20) must be 3 = IEEE float; mono; 32-bit.
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)), Is.EqualTo(3));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(32));

    // Left channel samples: exact float bytes (i * 0.1f).
    for (var i = 0; i < 8; ++i)
      Assert.That(BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(44 + i * 4)), Is.EqualTo(i * 0.1f));
  }

  [Test]
  public void Float64_ExtractedChannelIsFloat64Wav() {
    var blob = MakeFloatStereoWav(64);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new WavFormatDescriptor().ExtractEntry(ms, "RIGHT.wav", output, null);
    var wav = output.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)), Is.EqualTo(3));  // float
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));  // mono
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(64)); // 64-bit

    // Right channel samples: exact double bytes (i * -0.2).
    for (var i = 0; i < 8; ++i)
      Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(wav.AsSpan(44 + i * 8)), Is.EqualTo(i * -0.2));
  }
}
