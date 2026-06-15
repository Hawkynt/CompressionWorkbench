#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.WsAdpcm;
using FileFormat.Apc;

namespace Compression.Tests.Apc;

[TestFixture]
public class ApcTests {

  private static byte[] BuildApc(byte[] data, int rate, int leftInit, int rightInit, bool stereo) {
    using var ms = new MemoryStream();
    var header = new byte[32];
    "CRYO_APC"u8.CopyTo(header);
    Encoding.ASCII.GetBytes("1.20").CopyTo(header.AsSpan(8));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(data.Length * 2)); // sampleCount-ish
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)rate);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)leftInit);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)rightInit);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), stereo ? 1u : 0u);
    ms.Write(header);
    ms.Write(data);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    var apc = BuildApc([0x04, 0x10], 22050, leftInit: 0, rightInit: 0, stereo: false);
    using var ms = new MemoryStream(apc);
    var entries = new ApcFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.apc").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_Mono_DecodesKnownNibbles() {
    // Initial predictor 0, step index 0. byte 0x04 → low nibble 4 (predictor 7), high nibble 0 (predictor 8).
    var apc = BuildApc([0x04], 22050, leftInit: 0, rightInit: 0, stereo: false);
    using var ms = new MemoryStream(apc);
    using var output = new MemoryStream();
    new ApcFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    var state = new StandardImaCodec.State(0, 0);
    var expected = StandardImaCodec.Decode([0x04], ref state);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)), Is.EqualTo(expected[0]));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(46)), Is.EqualTo(expected[1]));
  }

  [Test]
  public void Descriptor_InitialPredictorSeedsFirstSample() {
    // Initial predictor 5000; first low nibble 0 → diff = step>>3 = 0, sample stays 5000.
    var apc = BuildApc([0x00], 22050, leftInit: 5000, rightInit: 0, stereo: false);
    using var ms = new MemoryStream(apc);
    using var output = new MemoryStream();
    new ApcFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)), Is.EqualTo(5000));
  }

  [Test]
  public void Descriptor_Stereo_SurfacesLeftRight() {
    var apc = BuildApc(new byte[16], 22050, leftInit: 0, rightInit: 0, stereo: true);
    using var ms = new MemoryStream(apc);
    var entries = new ApcFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  [Test]
  public void Descriptor_Stereo_NibbleInterleaveLeftLowRightHigh() {
    // byte 0x40 → low nibble 0 (left, small), high nibble 4 (right, big step).
    // Left predictor starts 0, nibble 0 → 0. Right predictor starts 1000, nibble 4 → 1000+7=1007.
    var apc = BuildApc([0x40], 22050, leftInit: 0, rightInit: 1000, stereo: true);
    using var ms = new MemoryStream(apc);
    using var leftOut = new MemoryStream();
    new ApcFormatDescriptor().ExtractEntry(ms, "LEFT.wav", leftOut, null);
    var left = leftOut.ToArray();
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(left.AsSpan(44)), Is.EqualTo(0));

    using var ms2 = new MemoryStream(apc);
    using var rightOut = new MemoryStream();
    new ApcFormatDescriptor().ExtractEntry(ms2, "RIGHT.wav", rightOut, null);
    var right = rightOut.ToArray();
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(right.AsSpan(44)), Is.EqualTo(1007));
  }

  [Test]
  public void Descriptor_BadMagic_Throws() {
    var apc = BuildApc([0x04], 22050, 0, 0, false);
    apc[0] = (byte)'X';
    using var ms = new MemoryStream(apc);
    Assert.That(() => new ApcFormatDescriptor().List(ms, null), Throws.Exception);
  }
}
