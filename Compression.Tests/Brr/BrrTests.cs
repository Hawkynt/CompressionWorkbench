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

  private static byte[] SampleBrr(int sourceBlocks = 4) {
    var pcm = new short[BrrCodec.SamplesPerBlock * sourceBlocks];
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
    const int sourceBlocks = 4;
    using var ms = new MemoryStream(SampleBrr(sourceBlocks));
    using var output = new MemoryStream();
    new BrrFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(32000u));

    // BRRtools-compatible encoding emits one silent predictor-primer block because the
    // first source block is non-zero.
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)((sourceBlocks + 1) * BrrCodec.SamplesPerBlock * 2)));
  }

  [Test]
  public void LoopPointHeader_IsSkippedAndReported() {
    // Prepend a 2-byte LE loop-point header → (length % 9) == 2.
    // Two source blocks encode as three physical blocks because BRRtools adds the primer.
    var body = SampleBrr(2);
    var withHeader = new byte[2 + body.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(withHeader.AsSpan(0), 9); // loop point
    body.CopyTo(withHeader, 2);

    using var ms = new MemoryStream(withHeader);
    using var output = new MemoryStream();
    new BrrFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(3 * BrrCodec.SamplesPerBlock * 2)));

    using var iniIn = new MemoryStream(withHeader);
    using var iniOut = new MemoryStream();
    new BrrFormatDescriptor().ExtractEntry(iniIn, "metadata.ini", iniOut, null);
    var ini = Encoding.UTF8.GetString(iniOut.ToArray());
    Assert.That(ini, Does.Contain("loop_point=9"));
  }

  [Test]
  public void Create_FromMonoWav_ProducesBrrToolsFraming() {
    const int sourceBlocks = 3;
    const int samples = BrrCodec.SamplesPerBlock * sourceBlocks;
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 5.0) * 6000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 32000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("voice.wav", wav) };
    using var output = new MemoryStream();
    new BrrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var brr = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(brr.Length, Is.EqualTo((sourceBlocks + 1) * BrrCodec.BlockSize));
      Assert.That(brr.AsSpan(0, BrrCodec.BlockSize).ToArray(), Is.All.EqualTo((byte)0),
        "non-zero initial audio gets the BRRtools silent predictor-primer block");
      Assert.That(brr[^BrrCodec.BlockSize] & 0x01, Is.EqualTo(0x01), "last data block carries end flag");
    });
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