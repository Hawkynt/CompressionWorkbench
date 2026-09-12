#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Ast;

namespace Compression.Tests.Ast;

[TestFixture]
public class AstTests {

  private static short[] MakeTone(int n, double period, double amp, double phase = 0) {
    var result = new short[n];
    for (var i = 0; i < n; ++i)
      result[i] = (short)(Math.Sin(i * 2.0 * Math.PI / period + phase) * amp);
    return result;
  }

  private static byte[] MonoWav(short[] samples, int sampleRate) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16);
  }

  [Test]
  public void Writer_UsesCanonicalHeaderAndPerChannelBlockSize() {
    var blob = new AstWriter().Write([MakeTone(6000, 50, 10000)], 32000, loop: true, loopStart: 16, loopEnd: 5000);

    Assert.Multiple(() => {
      Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("STRM"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4)), Is.EqualTo((uint)(blob.Length - 0x40)));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(8)), Is.EqualTo((ushort)AstCodec.Pcm16BigEndian));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(10)), Is.EqualTo(16));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(14)), Is.EqualTo(ushort.MaxValue));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(32)), Is.EqualTo((uint)AstWriter.BlockSize));
      Assert.That(blob[0x28], Is.EqualTo(0x7F));
      Assert.That(blob.AsSpan(0x40, 4).ToArray(), Is.EqualTo("BLCK"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0x44)), Is.EqualTo((uint)AstWriter.BlockSize));
    });
  }

  [Test]
  public void Writer_Reader_RoundTripsExactly_Pcm16() {
    var left = MakeTone(20000, 50.0, 11000);
    var right = MakeTone(20000, 80.0, 9000, Math.PI / 3);
    var blob = new AstWriter().Write([left, right], 32000);
    var parsed = new AstReader().Read(blob);

    Assert.Multiple(() => {
      Assert.That(parsed.Info.NumChannels, Is.EqualTo(2));
      Assert.That(parsed.Info.SampleRate, Is.EqualTo(32000));
      Assert.That(parsed.Info.Codec, Is.EqualTo((int)AstCodec.Pcm16BigEndian));
      Assert.That(parsed.Info.SampleCount, Is.EqualTo(20000));
      Assert.That(parsed.Pcm[0], Is.EqualTo(left));
      Assert.That(parsed.Pcm[1], Is.EqualTo(right));
    });
  }

  [Test]
  public void Writer_MultiBlock_RoundTripsExactly() {
    // 0x2760 bytes/channel = 5040 PCM16 samples/block.
    var mono = MakeTone(12000, 55, 11000);
    var parsed = new AstReader().Read(new AstWriter().Write([mono], 32000));
    Assert.That(parsed.Blocks.Count, Is.EqualTo(3));
    Assert.That(parsed.Pcm[0], Is.EqualTo(mono));
  }

  [Test]
  public void Writer_Afc_EncodesMultipleBlocksAndWritesHistories() {
    var source = MakeTone(1000, 67, 14000);
    var options = new AstWriterOptions(AstCodec.Afc, BlockSize: 9 * 32);
    var blob = new AstWriter().Write([source], 32000, options);
    var parsed = new AstReader().Read(blob);

    var meanAbsoluteError = source.Zip(parsed.Pcm[0], static (a, b) => Math.Abs(a - b)).Average();
    Assert.Multiple(() => {
      Assert.That(parsed.Info.Codec, Is.EqualTo((int)AstCodec.Afc));
      Assert.That(parsed.Blocks.Count, Is.GreaterThan(1));
      Assert.That(parsed.Blocks[1].HeaderData.Take(4).Any(static value => value != 0), Is.True);
      Assert.That(meanAbsoluteError, Is.LessThan(100.0));
    });
  }

  [Test]
  public void Descriptor_ListsFullAndPerChannelAndMetadata() {
    var blob = new AstWriter().Write([MakeTone(5000, 40, 10000), MakeTone(5000, 60, 8000)], 22050);
    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.ast" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_Metadata_DescribesStreamGeometry() {
    var blob = new AstWriter().Write([MakeTone(1000, 25, 8000)], 48000,
      new AstWriterOptions(AstCodec.Pcm16BigEndian, 512, Volume: 91));
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AstFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(ini, Does.Contain("sampleRate=48000"));
    Assert.That(ini, Does.Contain("codec=PCM16BE"));
    Assert.That(ini, Does.Contain("blockSize=512"));
    Assert.That(ini, Does.Contain("volume=91"));
  }

  private static byte[] BuildAfcAst(byte[] chFrames0, byte[] chFrames1, int sampleCount, int sampleRate) {
    if (chFrames0.Length != chFrames1.Length)
      throw new ArgumentException("equal per-channel block sizes");
    var blockSize = chFrames0.Length;

    var header = new byte[0x40];
    "STRM"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 0);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 16);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), 2);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)sampleCount);

    var block = new byte[32];
    "BLCK"u8.CopyTo(block);
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), (uint)blockSize);
    return [.. header, .. block, .. chFrames0, .. chFrames1];
  }

  [Test]
  public void Descriptor_AfcCodec_DecodesPerChannel() {
    var left = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x70, 0x00, 0x00, 0x00, 0x00 };
    var right = new byte[] { 0x00, 0x21, 0x43, 0x65, 0x07, 0x00, 0x00, 0x00, 0x00 };
    var blob = BuildAfcAst(left, right, sampleCount: 16, sampleRate: 32000);

    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);

    using var ms2 = new MemoryStream(blob);
    using var wav = new MemoryStream();
    new AstFormatDescriptor().ExtractEntry(ms2, "LEFT.wav", wav, null);
    var parsed = new FileFormat.Wav.WavReader().Read(wav.ToArray());
    var samples = new short[parsed.InterleavedPcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 2));
    Assert.That(samples[..8], Is.EqualTo(new short[] { 1, 2, 3, 4, 5, 6, 7, 0 }));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "STRM"u8.ToArray().Concat(new byte[4]).ToArray();
    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ast"));
  }

  [Test]
  public void Create_FromPerChannelWavs_RoundTripsExactly() {
    var left = MakeTone(9000, 45, 10000);
    var right = MakeTone(9000, 70, 9000, 1.1);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", MonoWav(left, 24000)),
      ArchiveInputInfo.InMemory("RIGHT.wav", MonoWav(right, 24000)),
    };

    using var output = new MemoryStream();
    new AstFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new AstReader().Read(output.ToArray());
    Assert.That(parsed.Pcm[0], Is.EqualTo(left));
    Assert.That(parsed.Pcm[1], Is.EqualTo(right));
  }

  [Test]
  public void Create_MetadataSelectsAfcLoopBlockSizeAndVolume() {
    var source = MakeTone(1000, 59, 12000);
    var metadata = Encoding.UTF8.GetBytes("""
      [ast]
      codec=AFC
      blockSize=288
      volume=99
      loop=1
      loopStart=16
      loopEnd=800
      """);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", MonoWav(source, 32000)),
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
    };

    using var output = new MemoryStream();
    new AstFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new AstReader().Read(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.Info.Codec, Is.EqualTo((int)AstCodec.Afc));
      Assert.That(parsed.Info.Loop, Is.True);
      Assert.That(parsed.Info.LoopStart, Is.EqualTo(16));
      Assert.That(parsed.Info.LoopEnd, Is.EqualTo(800));
      Assert.That(parsed.Info.FirstBlockSize, Is.EqualTo(288));
      Assert.That(parsed.Info.Volume, Is.EqualTo(99));
    });
  }

  [TestCase("pcm16be", 8, 12345, true)]
  [TestCase("afc", 6, 32000, true)]
  [TestCase("afc", 7, 32000, false)]
  public void AudioTarget_ReportsFeasibleChannelAndRateCombinations(string codec, int channels, int sampleRate, bool expected) {
    var descriptor = new AstFormatDescriptor();
    var format = new AudioPcmFormat(sampleRate, channels, 16);
    Assert.That(descriptor.CanEncode(format, codec, new FormatCreateOptions(), out _), Is.EqualTo(expected));
  }

  [Test]
  public void AudioTarget_RejectsCodecMisalignedBlockSizes() {
    var descriptor = new AstFormatDescriptor();
    var format = new AudioPcmFormat(32000, 2, 16);
    Assert.That(descriptor.CanEncode(format, "pcm16be", new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "511" },
    }, out _), Is.False);
    Assert.That(descriptor.CanEncode(format, "afc", new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "287" },
    }, out _), Is.False);
  }

  [TestCase(AstCodec.Pcm16BigEndian, 10080)]
  [TestCase(AstCodec.Afc, 288)]
  public void DemuxMux_RoundTripsCanonicalAstByteExactly(AstCodec codec, int blockSize) {
    var left = MakeTone(1200, 53, 13000);
    var right = MakeTone(1200, 79, 9000, 0.4);
    var original = new AstWriter().Write([left, right], 32000,
      new AstWriterOptions(codec, blockSize, Loop: true, LoopStart: 16, LoopEnd: 1000, Volume: 83));
    var descriptor = new AstFormatDescriptor();

    using var input = new MemoryStream(original);
    Assert.That(descriptor.TryDemux(input, out var encoded), Is.True);
    using var output = new MemoryStream();
    descriptor.Mux(output, encoded!, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Mux_ReblocksAfcWithoutChangingEncodedChannelBytes() {
    var original = new AstWriter().Write([
      MakeTone(1800, 61, 14000),
      MakeTone(1800, 83, 11000, 0.8),
    ], 32000, new AstWriterOptions(AstCodec.Afc, BlockSize: 288));
    var descriptor = new AstFormatDescriptor();

    using var input = new MemoryStream(original);
    Assert.That(descriptor.TryDemux(input, out var sourceStream), Is.True);
    var before = GatherPlanarChannels(sourceStream!);

    using var output = new MemoryStream();
    descriptor.Mux(output, sourceStream!, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "576" },
    });

    using var rebuilt = new MemoryStream(output.ToArray());
    Assert.That(descriptor.TryDemux(rebuilt, out var rebuiltStream), Is.True);
    var after = GatherPlanarChannels(rebuiltStream!);
    Assert.That(after[0], Is.EqualTo(before[0]));
    Assert.That(after[1], Is.EqualTo(before[1]));
    Assert.That(new AstReader().Read(output.ToArray()).Info.FirstBlockSize, Is.EqualTo(576));
  }

  [Test]
  public void TryDemux_RejectsTruncatedBlockPayload() {
    var blob = new AstWriter().Write([MakeTone(1000, 40, 10000)], 32000);
    Array.Resize(ref blob, blob.Length - 1);
    using var input = new MemoryStream(blob);
    Assert.That(new AstFormatDescriptor().TryDemux(input, out _), Is.False);
  }

  [Test]
  public void Create_FullPassthrough_ReturnsVerbatim() {
    var original = new AstWriter().Write([MakeTone(2000, 30, 8000)], 16000);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.ast", original) };
    using var output = new MemoryStream();
    new AstFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  private static byte[][] GatherPlanarChannels(AudioEncodedStream stream) {
    var result = Enumerable.Range(0, stream.Format.Channels).Select(_ => new List<byte>()).ToArray();
    foreach (var packet in stream.Packets) {
      var perChannel = packet.Data.Length / stream.Format.Channels;
      for (var channel = 0; channel < result.Length; ++channel)
        result[channel].AddRange(packet.Data.AsSpan(channel * perChannel, perChannel).ToArray());
    }
    return result.Select(static bytes => bytes.ToArray()).ToArray();
  }
}
