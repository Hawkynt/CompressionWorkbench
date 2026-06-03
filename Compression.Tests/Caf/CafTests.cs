#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Caf;

namespace Compression.Tests.Caf;

[TestFixture]
public class CafTests {

  // 44.1 kHz stereo 16-bit PCM, 10 frames, samples stored BIG-endian (default CAF flags=0).
  private static byte[] MakeStereoCaf() {
    const int frames = 10;
    var pcm = new byte[frames * 2 * 2]; // big-endian interleaved
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16BigEndian(pcm.AsSpan(i * 4), (short)(i * 100));      // left
      BinaryPrimitives.WriteInt16BigEndian(pcm.AsSpan(i * 4 + 2), (short)(i * -100)); // right
    }
    return BuildCaf(sampleRate: 44100, channels: 2, bitsPerChannel: 16, formatFlags: 0, interleaved: pcm);
  }

  private static byte[] BuildCaf(double sampleRate, int channels, int bitsPerChannel, uint formatFlags, byte[] interleaved) {
    using var ms = new MemoryStream();
    Span<byte> hdr = stackalloc byte[8];
    "caff"u8.CopyTo(hdr);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], 1); // version
    BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], 0); // flags
    ms.Write(hdr);

    // desc chunk (32-byte body)
    var desc = new byte[32];
    BinaryPrimitives.WriteDoubleBigEndian(desc.AsSpan(0), sampleRate);
    "lpcm"u8.CopyTo(desc.AsSpan(8));
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(12), formatFlags);
    var bytesPerFrame = (uint)(channels * bitsPerChannel / 8);
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(16), bytesPerFrame); // mBytesPerPacket
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(20), 1);             // mFramesPerPacket
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(24), (uint)channels);
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(28), (uint)bitsPerChannel);
    WriteChunk(ms, "desc", desc);

    // data chunk: uint32 mEditCount + audio bytes
    var data = new byte[4 + interleaved.Length];
    interleaved.CopyTo(data.AsSpan(4));
    WriteChunk(ms, "data", data);

    return ms.ToArray();
  }

  private static void WriteChunk(Stream s, string type, byte[] body) {
    Span<byte> head = stackalloc byte[12];
    System.Text.Encoding.ASCII.GetBytes(type).CopyTo(head);
    BinaryPrimitives.WriteInt64BigEndian(head[4..], body.Length);
    s.Write(head);
    s.Write(body);
  }

  [Test]
  public void CafReader_ParsesDescAndData() {
    var blob = MakeStereoCaf();
    var parsed = new CafReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.IsFloat, Is.False);
    // 10 frames * 2 channels * 2 bytes; reader stores little-endian
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(40));
    // First left sample (frame 0 = 0), second left sample (frame 1 = 100) in LE
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(4)), Is.EqualTo((short)100));
  }

  [Test]
  public void Descriptor_List_SurfacesFullAndChannels() {
    var blob = MakeStereoCaf();
    using var ms = new MemoryStream(blob);
    var entries = new CafFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.caf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "FULL.caf").Kind, Is.EqualTo("Container"));
  }

  [Test]
  public void Descriptor_ExtractedChannelIsMonoWav() {
    var blob = MakeStereoCaf();
    var tmp = Path.Combine(Path.GetTempPath(), "caf_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new CafFormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav"]);
      var mono = File.ReadAllBytes(Path.Combine(tmp, "LEFT.wav"));
      // fmt chunk NumChannels field at offset 22 (uint16 LE)
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(mono.AsSpan(22)), Is.EqualTo(1));
      // Sample rate preserved at offset 24
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(24)), Is.EqualTo(44100u));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Descriptor_ExtractEntry_StreamsChannel() {
    var blob = MakeStereoCaf();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new CafFormatDescriptor().ExtractEntry(ms, "RIGHT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
  }

  // 8 kHz stereo μ-law, 6 frames; one source byte per channel sample, channels
  // interleaved bytewise exactly like LPCM (mBytesPerPacket = channels, 1 frame/packet).
  private static byte[] MakeStereoUlawCaf() {
    const int frames = 6;
    var data = new byte[frames * 2];
    for (var i = 0; i < frames; ++i) {
      data[i * 2] = (byte)(0x10 + i * 7);     // left
      data[i * 2 + 1] = (byte)(0x90 + i * 5); // right
    }
    return BuildCompandedCaf(formatId: "ulaw", sampleRate: 8000, channels: 2, interleaved: data);
  }

  private static byte[] BuildCompandedCaf(string formatId, double sampleRate, int channels, byte[] interleaved) {
    using var ms = new MemoryStream();
    Span<byte> hdr = stackalloc byte[8];
    "caff"u8.CopyTo(hdr);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], 1);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], 0);
    ms.Write(hdr);

    var desc = new byte[32];
    BinaryPrimitives.WriteDoubleBigEndian(desc.AsSpan(0), sampleRate);
    System.Text.Encoding.ASCII.GetBytes(formatId).CopyTo(desc.AsSpan(8));
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(12), 0);              // mFormatFlags
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(16), (uint)channels); // mBytesPerPacket (1 byte/channel)
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(20), 1);              // mFramesPerPacket
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(24), (uint)channels);
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(28), 8);              // mBitsPerChannel (companded)
    WriteChunk(ms, "desc", desc);

    var data = new byte[4 + interleaved.Length];
    interleaved.CopyTo(data.AsSpan(4));
    WriteChunk(ms, "data", data);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_Ulaw_SurfacesDecodedChannels() {
    var blob = MakeStereoUlawCaf();
    using var ms = new MemoryStream(blob);
    var entries = new CafFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.caf" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void CafReader_Ulaw_FirstSamplesMatchMuLawDecode() {
    var blob = MakeStereoUlawCaf();
    var parsed = new CafReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16)); // decoded to 16-bit
    Assert.That(parsed.IsFloat, Is.False);
    Assert.That(parsed.FormatId, Is.EqualTo("lpcm"));  // surfaced as canonical lpcm

    // First left/right samples decode via Codec.MuLaw from the first two source bytes.
    var leftExp = Codec.MuLaw.MuLawCodec.DecodeSample((byte)0x10);
    var rightExp = Codec.MuLaw.MuLawCodec.DecodeSample((byte)0x90);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(0)), Is.EqualTo(leftExp));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(2)), Is.EqualTo(rightExp));
  }

  [Test]
  public void Descriptor_CreateFromChannelWavs_RoundTrips() {
    const int frames = 8;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 11));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(-i * 7));
    }
    var leftWav = PcmCodec.ToWavBlob(left, channels: 1, sampleRate: 48000, bitsPerSample: 16);
    var rightWav = PcmCodec.ToWavBlob(right, channels: 1, sampleRate: 48000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav),
    };

    using var output = new MemoryStream();
    new CafFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var caf = output.ToArray();

    var parsed = new CafReader().Read(caf);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(48000));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));

    // Deinterleave and compare per-channel samples (reader is little-endian).
    for (var i = 0; i < frames; ++i) {
      var l = BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 4));
      var r = BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 4 + 2));
      Assert.That(l, Is.EqualTo((short)(i * 11)));
      Assert.That(r, Is.EqualTo((short)(-i * 7)));
    }
  }
}
