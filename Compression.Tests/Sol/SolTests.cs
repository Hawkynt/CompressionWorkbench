#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Codec.SolDpcm;
using Compression.Registry;
using FileFormat.Sol;

namespace Compression.Tests.Sol;

[TestFixture]
public class SolTests {

  private static byte[] BuildSol(ushort magic, int rate, byte type, byte[] data) {
    using var ms = new MemoryStream();
    var header = new byte[7];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), magic);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)rate);
    header[6] = type;
    ms.Write(header);
    ms.Write(data);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    var sol = BuildSol(0x0C8D, 22050, type: 0x01, data: new byte[] { 0, 0, 0, 0 }); // 16-bit PCM
    using var ms = new MemoryStream(sol);
    var entries = new SolFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.sol").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_Pcm16_Passthrough() {
    var data = new byte[8];
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0), 1000);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), -2000);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4), 32000);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(6), -32000);
    var sol = BuildSol(0x0C8D, 16000, type: 0x01, data: data);

    using var ms = new MemoryStream(sol);
    using var output = new MemoryStream();
    new SolFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)), Is.EqualTo(1000));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(46)), Is.EqualTo(-2000));
  }

  [Test]
  public void Descriptor_Pcm8_DecodesUnsigned() {
    var sol = BuildSol(0x0B8D, 11025, type: 0x00, data: new byte[] { 128, 255, 0 });
    using var ms = new MemoryStream(sol);
    using var output = new MemoryStream();
    new SolFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)), Is.EqualTo(0));
  }

  [Test]
  public void Descriptor_Dpcm8_DecodesViaTable() {
    // type bit2 = DPCM, 8-bit. magic 0x0B8D → "old" table.
    var sol = BuildSol(0x0B8D, 22050, type: 0x04, data: new byte[] { 0x4C });
    using var ms = new MemoryStream(sol);
    using var output = new MemoryStream();
    new SolFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    var expected = SolDpcmCodec.Decode([0x4C], SolDpcmCodec.Mode.Old8);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)), Is.EqualTo(expected[0]));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(46)), Is.EqualTo(expected[1]));
  }

  [Test]
  public void Descriptor_Stereo_SurfacesLeftRight() {
    var sol = BuildSol(0x0C8D, 22050, type: 0x03, data: new byte[8]); // 16-bit stereo
    using var ms = new MemoryStream(sol);
    var entries = new SolFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  [Test]
  public void Descriptor_Create_FromMonoWav_RoundTrips() {
    var pcm = new byte[128 * 2];
    for (var i = 0; i < 128; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 6.0) * 8000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 22050, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new SolFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var sol = created.ToArray();

    Assert.That((ushort)BinaryPrimitives.ReadUInt32LittleEndian(sol.AsSpan(0)), Is.EqualTo(0x0C8D));
    Assert.That(sol[6] & 0x01, Is.EqualTo(0x01)); // 16-bit flag

    using var reopen = new MemoryStream(sol);
    using var monoOut = new MemoryStream();
    new SolFormatDescriptor().ExtractEntry(reopen, "MONO.wav", monoOut, null);
    var mono = monoOut.ToArray();
    // Lossless 16-bit PCM passthrough: samples must match exactly.
    for (var i = 0; i < 128; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(mono.AsSpan(44 + i * 2)),
                  Is.EqualTo(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2))));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullSol() {
    var original = BuildSol(0x0C8D, 22050, type: 0x01, data: new byte[] { 1, 2, 3, 4 });
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.sol", original) };
    using var output = new MemoryStream();
    new SolFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
