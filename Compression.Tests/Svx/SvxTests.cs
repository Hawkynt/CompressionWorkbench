#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Svx;

namespace Compression.Tests.Svx;

[TestFixture]
public class SvxTests {

  private static byte[] Chunk(string id, byte[] body) {
    var pad = (body.Length & 1) == 1 ? 1 : 0;
    var r = new byte[8 + body.Length + pad];
    System.Text.Encoding.ASCII.GetBytes(id).CopyTo(r, 0);
    BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)body.Length);
    body.CopyTo(r, 8);
    return r;
  }

  private static byte[] Vhdr(uint oneShot, int sampleRate, int compression) {
    var b = new byte[20];
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), oneShot);
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0);
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 0);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), (ushort)sampleRate);
    b[14] = 1;                  // octaves
    b[15] = (byte)compression;
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 0x10000);
    return b;
  }

  private static byte[] Form(params byte[][] chunks) {
    using var inner = new MemoryStream();
    foreach (var c in chunks) inner.Write(c);
    var innerBytes = inner.ToArray();
    var r = new byte[12 + innerBytes.Length];
    "FORM"u8.ToArray().CopyTo(r, 0);
    BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)(4 + innerBytes.Length));
    "8SVX"u8.ToArray().CopyTo(r, 8);
    innerBytes.CopyTo(r, 12);
    return r;
  }

  private static byte[] MakeMono8Svx() {
    // Four signed samples: 0, 10, -10, 127.
    var body = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    return Form(Chunk("VHDR", Vhdr(4, 8000, SvxReader.CompressionNone)),
                Chunk("CHAN", BigEndianU32(SvxReader.ChannelLeft)),
                Chunk("BODY", body));
  }

  private static byte[] MakeStereo8Svx() {
    // Planar halves: left = {1,2}, right = {-1,-2} (signed).
    var body = new byte[] { 1, 2, unchecked((byte)-1), unchecked((byte)-2) };
    return Form(Chunk("VHDR", Vhdr(2, 22050, SvxReader.CompressionNone)),
                Chunk("CHAN", BigEndianU32(SvxReader.ChannelStereo)),
                Chunk("BODY", body));
  }

  private static byte[] BigEndianU32(int v) {
    var b = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
    return b;
  }

  [Test]
  public void FibonacciDelta_DecodesKnownNibbleSequence() {
    // [pad, initial=0, 0x9A, 0x21]
    //   0x9A: +table[9]=+1 → 1, +table[10]=+2 → 3
    //   0x21: +table[2]=-13 → -10, +table[1]=-21 → -31
    var compressed = new byte[] { 0, 0, 0x9A, 0x21 };
    var decoded = SvxReader.DecodeFibonacciDelta(compressed);
    Assert.That(decoded, Is.EqualTo(new byte[] {
      1, 3, unchecked((byte)-10), unchecked((byte)-31),
    }));
  }

  [Test]
  public void Mono_ListsFullAndMonoChannel() {
    using var ms = new MemoryStream(MakeMono8Svx());
    var entries = new Svx8FormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.8svx").Kind, Is.EqualTo("Container"));
    var mono = entries.First(e => e.Name == "MONO.wav");
    Assert.That(mono.Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Mono_ChannelIsValidMonoWavWithRebiasedSamples() {
    using var ms = new MemoryStream(MakeMono8Svx());
    using var output = new MemoryStream();
    new Svx8FormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));   // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8000u)); // rate
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));    // 8-bit

    // Signed {0,10,-10,127} → unsigned {128,138,118,255}.
    var pcm = wav.AsSpan(44).ToArray();
    Assert.That(pcm, Is.EqualTo(new byte[] { 128, 138, 118, 255 }));
  }

  [Test]
  public void Stereo_SplitsPlanarHalvesIntoLeftAndRight() {
    using var ms = new MemoryStream(MakeStereo8Svx());
    var entries = new Svx8FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);

    using var left = new MemoryStream();
    new Svx8FormatDescriptor().ExtractEntry(new MemoryStream(MakeStereo8Svx()), "LEFT.wav", left, null);
    var leftPcm = left.ToArray().AsSpan(44).ToArray();
    Assert.That(leftPcm, Is.EqualTo(new byte[] { 129, 130 })); // {1,2} + 128

    using var right = new MemoryStream();
    new Svx8FormatDescriptor().ExtractEntry(new MemoryStream(MakeStereo8Svx()), "RIGHT.wav", right, null);
    var rightPcm = right.ToArray().AsSpan(44).ToArray();
    Assert.That(rightPcm, Is.EqualTo(new byte[] { 127, 126 })); // {-1,-2} + 128
  }

  [Test]
  public void Create_FromMonoWav_RoundTrips() {
    // 8-bit unsigned WAV {128,138,118,255} → signed {0,10,-10,127} in 8SVX.
    var wav = PcmCodec.ToWavBlob(new byte[] { 128, 138, 118, 255 }, 1, 8000, 8);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new Svx8FormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var svx = output.ToArray();

    var parsed = new SvxReader().Read(svx);
    Assert.That(parsed.SampleRate, Is.EqualTo(8000));
    Assert.That(parsed.Channels, Is.EqualTo(SvxReader.ChannelLeft));
    Assert.That(parsed.Body, Is.EqualTo(new byte[] { 0, 10, unchecked((byte)-10), 127 }));
  }

  [Test]
  public void Create_FromStereoWavs_RoundTrips() {
    var left = PcmCodec.ToWavBlob(new byte[] { 129, 130 }, 1, 22050, 8);  // {1,2}
    var right = PcmCodec.ToWavBlob(new byte[] { 127, 126 }, 1, 22050, 8); // {-1,-2}
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", left),
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
    };

    using var output = new MemoryStream();
    new Svx8FormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new SvxReader().Read(output.ToArray());

    Assert.That(parsed.Channels, Is.EqualTo(SvxReader.ChannelStereo));
    // Planar: left half then right half.
    Assert.That(parsed.Body, Is.EqualTo(new byte[] { 1, 2, unchecked((byte)-1), unchecked((byte)-2) }));
  }
}
