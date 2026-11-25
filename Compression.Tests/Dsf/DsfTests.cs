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

  // ── malformed headers: refused, and nothing sized from the header alone ──

  // Field offsets in the hand-crafted file: 'DSD ' chunk at 0, 'fmt ' at 28, 'data' at 80.
  private const int DsdChunkSizeAt = 4, FmtSizeAt = 32, ChannelNumAt = 52, SampleRateAt = 56,
    SampleCountAt = 64, BlockSizeAt = 72, DataSizeAt = 84;

  private static byte[] Patch32(byte[] dsf, int at, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(dsf.AsSpan(at), value);
    return dsf;
  }

  private static byte[] Patch64(byte[] dsf, int at, ulong value) {
    BinaryPrimitives.WriteUInt64LittleEndian(dsf.AsSpan(at), value);
    return dsf;
  }

  [Category("EdgeCase")]
  [TestCase(ulong.MaxValue)]
  [TestCase(1UL << 40)]
  [TestCase((ulong)long.MaxValue)]
  public void Given_SampleCountFarBeyondTheData_When_Read_Then_ChannelsHoldOnlyTheDataPresent(ulong sampleCount) {
    var dsf = Patch64(MakeStereoDsf(bytesPerChannel: 100, leftFill: 0x69, rightFill: 0x96), SampleCountAt, sampleCount);
    var parsed = new DsfReader().Read(dsf);
    Assert.That(parsed.ChannelDsd.Select(c => c.Length), Is.All.EqualTo(BlockSize));
    Assert.That(parsed.ChannelDsd[0][0], Is.EqualTo(0x69));
    Assert.That(parsed.ChannelDsd[1][0], Is.EqualTo(0x96));
  }

  [Category("Boundary")]
  [Test]
  public void Given_SampleCountExactlyTheData_When_Read_Then_ChannelsAreThatLong() {
    var dsf = Patch64(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), SampleCountAt, BlockSize * 8UL);
    Assert.That(new DsfReader().Read(dsf).ChannelDsd.Select(c => c.Length), Is.All.EqualTo(BlockSize));
  }

  [Category("Exception")]
  [TestCase(0u)]
  [TestCase(7u)]
  [TestCase(0x80000000u)]
  [TestCase(0xFFFFFFFFu)]
  public void Given_ChannelNumOutsideSpecAndData_When_Read_Then_InvalidData(uint channelNum)
    => Assert.Throws<InvalidDataException>(() =>
      new DsfReader().Read(Patch32(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), ChannelNumAt, channelNum)));

  [Category("Boundary")]
  [Test]
  public void Given_SixChannelsDeclaredOverTwoBlocks_When_Read_Then_MissingChannelsAreEmpty() {
    var parsed = new DsfReader().Read(Patch32(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), ChannelNumAt, 6));
    Assert.That(parsed.ChannelDsd.Select(c => c.Length), Is.EqualTo(new[] { 100, 100, 0, 0, 0, 0 }));
  }

  [Category("Exception")]
  [TestCase(0u)]
  [TestCase(0x80000000u)]
  public void Given_SampleRateOutsideRange_When_Read_Then_InvalidData(uint rate)
    => Assert.Throws<InvalidDataException>(() =>
      new DsfReader().Read(Patch32(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), SampleRateAt, rate)));

  [Category("Exception")]
  [TestCase(0x80000000u)]
  [TestCase(0u)]
  public void Given_BlockSizeOutsideRange_When_Read_Then_InvalidData(uint blockSize)
    => Assert.Throws<InvalidDataException>(() =>
      new DsfReader().Read(Patch32(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), BlockSizeAt, blockSize)));

  [Category("Exception")]
  [TestCase(0xFFFFFFFFUL)]
  [TestCase(0x1_0000_001CUL)]
  [TestCase(ulong.MaxValue)]
  public void Given_DsdChunkSizePastTheFile_When_Read_Then_InvalidData(ulong size)
    => Assert.Throws<InvalidDataException>(() =>
      new DsfReader().Read(Patch64(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), DsdChunkSizeAt, size)));

  [Category("Exception")]
  [TestCase(0xFFFFFFFFUL)]
  [TestCase(0xFFFFFFFF_FFFFFFF0UL)]
  [TestCase(51UL)]
  public void Given_FmtSizeOutsideTheFile_When_Read_Then_InvalidData(ulong size)
    => Assert.Throws<InvalidDataException>(() =>
      new DsfReader().Read(Patch64(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), FmtSizeAt, size)));

  [Category("Exception")]
  [TestCase(0x1_0000_000CUL)]
  [TestCase(ulong.MaxValue)]
  [TestCase(11UL)]
  public void Given_DataSizeOutsideTheFile_When_Read_Then_InvalidData(ulong size)
    => Assert.Throws<InvalidDataException>(() =>
      new DsfReader().Read(Patch64(MakeStereoDsf(bytesPerChannel: 100, leftFill: 1, rightFill: 2), DataSizeAt, size)));
}
