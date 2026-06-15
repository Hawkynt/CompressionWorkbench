#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Dsf;

namespace Compression.Tests.Dsf;

[TestFixture]
public class DsfTests {

  private const int BlockSize = 4096;
  private const int SampleRate = 2822400;

  /// <summary>
  /// Hand-crafts a stereo v1 raw-DSD DSF with <paramref name="bytesPerChannel"/> significant
  /// bytes per channel, the left channel filled with <paramref name="leftFill"/> and the right
  /// with <paramref name="rightFill"/>. Each channel occupies whole 4096-byte blocks
  /// (zero-padded), interleaved block round-robin: [L block][R block][L block]…
  /// </summary>
  private static byte[] MakeStereoDsf(int bytesPerChannel, byte leftFill, byte rightFill) {
    var blocksPerChannel = (bytesPerChannel + BlockSize - 1) / BlockSize;
    if (blocksPerChannel == 0) blocksPerChannel = 1;
    var sampleCount = (long)bytesPerChannel * 8;
    var payloadLen = (long)blocksPerChannel * BlockSize * 2;
    var dataChunkSize = 12 + payloadLen;
    var totalFileSize = 28 + 52 + dataChunkSize;

    var buf = new byte[totalFileSize];
    var s = buf.AsSpan();

    "DSD "u8.CopyTo(s);
    BinaryPrimitives.WriteUInt64LittleEndian(s[4..], 28);
    BinaryPrimitives.WriteUInt64LittleEndian(s[12..], (ulong)totalFileSize);
    BinaryPrimitives.WriteUInt64LittleEndian(s[20..], 0);

    var f = s[28..];
    "fmt "u8.CopyTo(f);
    BinaryPrimitives.WriteUInt64LittleEndian(f[4..], 52);
    BinaryPrimitives.WriteUInt32LittleEndian(f[12..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(f[16..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(f[20..], 2); // channelType stereo
    BinaryPrimitives.WriteUInt32LittleEndian(f[24..], 2); // channelNum
    BinaryPrimitives.WriteUInt32LittleEndian(f[28..], SampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(f[32..], 1); // bitsPerSample
    BinaryPrimitives.WriteUInt64LittleEndian(f[36..], (ulong)sampleCount);
    BinaryPrimitives.WriteUInt32LittleEndian(f[44..], BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(f[48..], 0);

    var d = s[(28 + 52)..];
    "data"u8.CopyTo(d);
    BinaryPrimitives.WriteUInt64LittleEndian(d[4..], (ulong)dataChunkSize);

    var payload = d[12..];
    for (var b = 0; b < blocksPerChannel; ++b) {
      var srcOff = b * BlockSize;
      var copy = Math.Min(BlockSize, bytesPerChannel - srcOff);
      // Left block then right block.
      var lBlock = payload.Slice((b * 2) * BlockSize, BlockSize);
      var rBlock = payload.Slice((b * 2 + 1) * BlockSize, BlockSize);
      for (var i = 0; i < copy; ++i) { lBlock[i] = leftFill; rBlock[i] = rightFill; }
    }

    return buf;
  }

  private static short FirstSample(byte[] wav) => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));

  [Test]
  public void List_SurfacesContainerStreamsAndChannels() {
    var blob = MakeStereoDsf(bytesPerChannel: 1024, leftFill: 0xFF, rightFill: 0x00);
    using var ms = new MemoryStream(blob);
    var entries = new DsfFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.dsf").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "LEFT.dsd").Kind, Is.EqualTo("Stream"));
    Assert.That(entries.First(e => e.Name == "RIGHT.dsd").Kind, Is.EqualTo("Stream"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "RIGHT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Streams_DeinterleaveBlockWise() {
    // Distinguishable per-channel patterns: left 0xAA, right 0x55.
    var blob = MakeStereoDsf(bytesPerChannel: 1024, leftFill: 0xAA, rightFill: 0x55);
    var parsed = new DsfReader().Read(blob);

    Assert.That(parsed.ChannelNum, Is.EqualTo(2));
    Assert.That(parsed.ChannelDsd[0].Length, Is.EqualTo(1024));
    Assert.That(parsed.ChannelDsd[1].Length, Is.EqualTo(1024));
    Assert.That(parsed.ChannelDsd[0].All(b => b == 0xAA), Is.True, "Left channel must de-interleave to 0xAA.");
    Assert.That(parsed.ChannelDsd[1].All(b => b == 0x55), Is.True, "Right channel must de-interleave to 0x55.");
  }

  [Test]
  public void Channels_DecimateToCorrectSignAndRate() {
    var blob = MakeStereoDsf(bytesPerChannel: 1024, leftFill: 0xFF, rightFill: 0x00);
    using var ms = new MemoryStream(blob);
    var tmp = Path.Combine(Path.GetTempPath(), "dsf_" + Guid.NewGuid().ToString("N"));
    try {
      new DsfFormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav", "RIGHT.wav"]);
      var left = File.ReadAllBytes(Path.Combine(tmp, "LEFT.wav"));
      var right = File.ReadAllBytes(Path.Combine(tmp, "RIGHT.wav"));

      // Valid mono RIFF at rate/64.
      Assert.That(left.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(22)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(left.AsSpan(24)), Is.EqualTo((uint)(SampleRate / 64)));

      // All-ones DSD → strongly positive PCM; all-zeros → strongly negative.
      Assert.That(FirstSample(left), Is.GreaterThan(0));
      Assert.That(FirstSample(right), Is.LessThan(0));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Channels_AlternatingBitsAreNearSilence() {
    // 0xAA = 10101010 → 32 ones, 32 zeros per 64-bit window → sum 0 → midpoint.
    var blob = MakeStereoDsf(bytesPerChannel: 1024, leftFill: 0xAA, rightFill: 0xAA);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new DsfFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    Assert.That(FirstSample(output.ToArray()), Is.EqualTo(0));
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
    new DsfFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    var parsed = new DsfReader().Read(created.ToArray());
    Assert.That(parsed.ChannelNum, Is.EqualTo(2));
    Assert.That(parsed.ChannelDsd[0], Is.EqualTo(left));
    Assert.That(parsed.ChannelDsd[1], Is.EqualTo(right));
  }

  [Test]
  public void Create_PassthroughFullDsf() {
    var blob = MakeStereoDsf(bytesPerChannel: 1024, leftFill: 0x12, rightFill: 0x34);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.dsf", blob) };
    using var created = new MemoryStream();
    new DsfFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    Assert.That(created.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void DefaultNames_StereoAreLeftRight() {
    Assert.That(ChannelLayout.DefaultNames(2), Is.EqualTo(new[] { "LEFT", "RIGHT" }));
  }
}
