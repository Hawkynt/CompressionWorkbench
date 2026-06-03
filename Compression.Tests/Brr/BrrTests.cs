#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Brr;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Brr;

namespace Compression.Tests.Brr;

[TestFixture]
public class BrrTests {

  private static byte[] SampleBrr(int blocks = 4) {
    var pcm = new short[BrrCodec.SamplesPerBlock * blocks];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 32) * 8000);
    return BrrCodec.Encode(pcm);
  }

  [Test]
  public void List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(SampleBrr());
    var entries = new BrrFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.brr").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void MonoWav_HasDefaultRateAndDecodedLength() {
    const int blocks = 4;
    using var ms = new MemoryStream(SampleBrr(blocks));
    using var output = new MemoryStream();
    new BrrFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(32000u));

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(blocks * BrrCodec.SamplesPerBlock * 2)));
  }

  [Test]
  public void LoopPointHeader_IsSkippedAndReported() {
    // Prepend a 2-byte LE loop-point header → (length % 9) == 2.
    var body = SampleBrr(2);
    var withHeader = new byte[2 + body.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(withHeader.AsSpan(0), 9); // loop point
    body.CopyTo(withHeader, 2);

    using var ms = new MemoryStream(withHeader);
    using var output = new MemoryStream();
    new BrrFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    // Two blocks decode to 32 samples despite the 2-byte header.
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(2 * BrrCodec.SamplesPerBlock * 2)));

    using var iniIn = new MemoryStream(withHeader);
    using var iniOut = new MemoryStream();
    new BrrFormatDescriptor().ExtractEntry(iniIn, "metadata.ini", iniOut, null);
    var ini = Encoding.UTF8.GetString(iniOut.ToArray());
    Assert.That(ini, Does.Contain("loop_point=9"));
  }

  [Test]
  public void Create_FromMonoWav_ProducesBrrBlocks() {
    const int samples = BrrCodec.SamplesPerBlock * 3;
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 5.0) * 6000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 32000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("voice.wav", wav) };
    using var output = new MemoryStream();
    new BrrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var brr = output.ToArray();

    Assert.That(brr.Length, Is.EqualTo(3 * BrrCodec.BlockSize));
    Assert.That(brr[^BrrCodec.BlockSize] & 0x01, Is.EqualTo(0x01), "last block carries end flag");
  }

  [Test]
  public void Create_PassthroughFullBrr() {
    var original = SampleBrr(2);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.brr", original) };
    using var output = new MemoryStream();
    new BrrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
