#pragma warning disable CS1591
using Codec.Bonk;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Bonk;

namespace Compression.Tests.Bonk;

[TestFixture]
public class BonkFormatWriteTests {

  [Test]
  public void Create_PassesThroughFullBonk() {
    var pcm = MakeInterleavedStereoPcm(256);
    var bonk = BonkCodec.Compress(pcm, channels: 2, sampleRate: 44100);
    var inputs = new[] { ArchiveInputInfo.InMemory("FULL.bonk", bonk) };
    using var output = new MemoryStream();

    new BonkFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(bonk));
  }

  [Test]
  public void Create_AssemblesStereoChannelWavs_Losslessly() {
    const int frames = 512;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var i = 0; i < frames; ++i) {
      var l = (short)(Math.Sin(i * 2 * Math.PI / 43) * 12000);
      var r = (short)(Math.Cos(i * 2 * Math.PI / 67) * 9000);
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), l);
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), r);
    }

    var inputs = new[] {
      ArchiveInputInfo.InMemory("LEFT.wav", PcmCodec.ToWavBlob(left, 1, 44100, 16, formatCode: 1)),
      ArchiveInputInfo.InMemory("RIGHT.wav", PcmCodec.ToWavBlob(right, 1, 44100, 16, formatCode: 1)),
    };

    using var output = new MemoryStream();
    var descriptor = new BonkFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    var decoded = BonkCodec.Decompress(output.ToArray());
    Assert.That(decoded, Is.EqualTo(PcmCodec.Interleave([left, right], 16)));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  private static byte[] MakeInterleavedStereoPcm(int frames) {
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var i = 0; i < frames; ++i) {
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 23 - 2000));
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(2000 - i * 17));
    }
    return PcmCodec.Interleave([left, right], 16);
  }
}
