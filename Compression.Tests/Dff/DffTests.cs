#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Dff;

namespace Compression.Tests.Dff;

[TestFixture]
public class DffTests {

  private const int SampleRate = 2822400;

  private static void AppendChunk(MemoryStream s, string ckId, byte[] body) {
    var head = new byte[12];
    Encoding.ASCII.GetBytes(ckId).CopyTo(head, 0);
    BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(4), (ulong)body.Length);
    s.Write(head);
    s.Write(body);
    if ((body.Length & 1) != 0) s.WriteByte(0);
  }

  private static byte[] Be32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }

  /// <summary>
  /// Hand-crafts a stereo uncompressed DSDIFF where the left channel is filled with
  /// <paramref name="leftFill"/> and the right with <paramref name="rightFill"/>; sample data is
  /// interleaved byte round-robin (L,R,L,R,…). Set <paramref name="compression"/> to "DST " to
  /// model a DST-compressed file (no de-interleavable PCM).
  /// </summary>
  private static byte[] MakeStereoDff(int bytesPerChannel, byte leftFill, byte rightFill, string compression = "DSD ") {
    // PROP/SND  body.
    using var prop = new MemoryStream();
    prop.Write("SND "u8);
    AppendChunk(prop, "FS  ", Be32(SampleRate));

    using var chnl = new MemoryStream();
    var cnt = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(cnt, 2); chnl.Write(cnt);
    chnl.Write("SLFT"u8); chnl.Write("SRGT"u8);
    AppendChunk(prop, "CHNL", chnl.ToArray());

    using var cmpr = new MemoryStream();
    cmpr.Write(Encoding.ASCII.GetBytes(compression));
    var nm = "not compressed"u8.ToArray();
    cmpr.WriteByte((byte)nm.Length); cmpr.Write(nm);
    AppendChunk(prop, "CMPR", cmpr.ToArray());

    // Byte round-robin sample data.
    var payload = new byte[bytesPerChannel * 2];
    for (var i = 0; i < bytesPerChannel; ++i) { payload[i * 2] = leftFill; payload[i * 2 + 1] = rightFill; }

    using var form = new MemoryStream();
    form.Write("DSD "u8); // form type
    AppendChunk(form, "FVER", Be32(0x01050000));
    AppendChunk(form, "PROP", prop.ToArray());
    AppendChunk(form, "DSD ", payload);

    var formBody = form.ToArray();
    using var file = new MemoryStream();
    var head = new byte[12];
    "FRM8"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(4), (ulong)formBody.Length);
    file.Write(head);
    file.Write(formBody);
    return file.ToArray();
  }

  private static short FirstSample(byte[] wav) => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));

  [Test]
  public void List_SurfacesContainerStreamsAndChannels() {
    var blob = MakeStereoDff(bytesPerChannel: 1024, leftFill: 0xFF, rightFill: 0x00);
    using var ms = new MemoryStream(blob);
    var entries = new DffFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.dff").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "LEFT.dsd").Kind, Is.EqualTo("Stream"));
    Assert.That(entries.First(e => e.Name == "RIGHT.dsd").Kind, Is.EqualTo("Stream"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "RIGHT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Streams_DeinterleaveByteWise() {
    var blob = MakeStereoDff(bytesPerChannel: 1024, leftFill: 0xAA, rightFill: 0x55);
    var parsed = new DffReader().Read(blob);

    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.ChannelDsd[0].Length, Is.EqualTo(1024));
    Assert.That(parsed.ChannelDsd[0].All(b => b == 0xAA), Is.True, "Left channel must de-interleave to 0xAA.");
    Assert.That(parsed.ChannelDsd[1].All(b => b == 0x55), Is.True, "Right channel must de-interleave to 0x55.");
  }

  [Test]
  public void Channels_DecimateToCorrectSignAndRate() {
    var blob = MakeStereoDff(bytesPerChannel: 1024, leftFill: 0xFF, rightFill: 0x00);
    using var ms = new MemoryStream(blob);
    var tmp = Path.Combine(Path.GetTempPath(), "dff_" + Guid.NewGuid().ToString("N"));
    try {
      new DffFormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav", "RIGHT.wav"]);
      var left = File.ReadAllBytes(Path.Combine(tmp, "LEFT.wav"));
      var right = File.ReadAllBytes(Path.Combine(tmp, "RIGHT.wav"));

      Assert.That(left.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(22)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(left.AsSpan(24)), Is.EqualTo((uint)(SampleRate / 64)));

      Assert.That(FirstSample(left), Is.GreaterThan(0));
      Assert.That(FirstSample(right), Is.LessThan(0));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void DstCompression_FallsBackToFullAndMetadataOnly() {
    var blob = MakeStereoDff(bytesPerChannel: 1024, leftFill: 0xFF, rightFill: 0x00, compression: "DST ");
    using var ms = new MemoryStream(blob);
    var entries = new DffFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.dff"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Stream"), Is.False, "DST-compressed DSDIFF must not surface raw streams.");
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False, "DST-compressed DSDIFF must not surface decoded channels.");
  }

  [Test]
  public void Create_RoundTripsRawDsdBitExact() {
    var left = Enumerable.Repeat((byte)0xAA, 1024).ToArray();
    var right = Enumerable.Repeat((byte)0x55, 1024).ToArray();

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.dsd", left),
      ArchiveInputInfo.InMemory("RIGHT.dsd", right),
    };

    using var created = new MemoryStream();
    new DffFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    var parsed = new DffReader().Read(created.ToArray());
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.ChannelDsd[0], Is.EqualTo(left));
    Assert.That(parsed.ChannelDsd[1], Is.EqualTo(right));
    Assert.That(parsed.SampleRate, Is.EqualTo(SampleRate));
  }

  [Test]
  public void Create_PassthroughFullDff() {
    var blob = MakeStereoDff(bytesPerChannel: 1024, leftFill: 0x12, rightFill: 0x34);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.dff", blob) };
    using var created = new MemoryStream();
    new DffFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    Assert.That(created.ToArray(), Is.EqualTo(blob));
  }
}
