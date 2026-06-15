#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Codec.RoqDpcm;
using Compression.Registry;
using FileFormat.Roq;

namespace Compression.Tests.Roq;

[TestFixture]
public class RoqTests {

  private static byte[] BuildRoq(ushort soundId, ushort arg, byte[] payload, params (ushort Id, byte[] Body)[] extra) {
    using var ms = new MemoryStream();
    // File signature chunk: id 0x1084, size 0xFFFFFFFF, arg 0x1E.
    var sig = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(sig.AsSpan(0), 0x1084);
    BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(2), 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(sig.AsSpan(6), 0x1E);
    ms.Write(sig);

    void WriteChunk(ushort id, ushort a, byte[] body) {
      var h = new byte[8];
      BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0), id);
      BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2), (uint)body.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), a);
      ms.Write(h);
      ms.Write(body);
    }

    WriteChunk(soundId, arg, payload);
    foreach (var (id, body) in extra)
      WriteChunk(id, 0, body);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    var roq = BuildRoq(0x1020, arg: 0, payload: [3, 2, 130]);
    using var ms = new MemoryStream(roq);
    var entries = new RoqFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.roq").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_MonoSound_DecodesSquareTable() {
    var roq = BuildRoq(0x1020, arg: 0, payload: [3, 2, 130]);
    using var ms = new MemoryStream(roq);
    using var output = new MemoryStream();
    new RoqFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    var expected = RoqDpcmCodec.Decode([3, 2, 130], 0, stereo: false);
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)), Is.EqualTo(expected[i]));
  }

  [Test]
  public void Descriptor_StereoSound_SurfacesLeftRight() {
    var roq = BuildRoq(0x1021, arg: 0x0102, payload: [1, 1, 2, 2]);
    using var ms = new MemoryStream(roq);
    var entries = new RoqFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  [Test]
  public void Descriptor_CountsVideoChunksInMetadata() {
    var roq = BuildRoq(0x1020, arg: 0, payload: [1, 2, 3],
      (0x1001, new byte[] { 0, 0, 0, 0 }),
      (0x1002, new byte[] { 0, 0 }),
      (0x1011, new byte[] { 0, 0, 0 }));
    using var ms = new MemoryStream(roq);
    using var output = new MemoryStream();
    new RoqFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var meta = System.Text.Encoding.UTF8.GetString(output.ToArray());
    Assert.That(meta, Does.Contain("video_chunks=3"));
    Assert.That(meta, Does.Contain("sound_chunks=1"));
  }

  [Test]
  public void Descriptor_Create_FromMonoWav_RoundTrips() {
    const int n = 200;
    var pcm = new byte[n * 2];
    for (var i = 0; i < n; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 8.0) * 6000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 22050, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new RoqFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var roq = created.ToArray();

    // File signature present.
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(roq.AsSpan(0)), Is.EqualTo(0x1084));

    using var reopen = new MemoryStream(roq);
    var entries = new RoqFormatDescriptor().List(reopen, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
  }

  [Test]
  public void Descriptor_Create_PassthroughFullRoq() {
    var original = BuildRoq(0x1020, arg: 0, payload: [5, 6, 7]);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.roq", original) };
    using var output = new MemoryStream();
    new RoqFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
