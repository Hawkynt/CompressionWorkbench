using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Mp3;
using FileFormat.Wav;
using FileFormat.WavPack;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AudioPacketAdapterTests {

  [Test]
  public void Mp3ResolverRoute_PreservesFramesAndExcludesId3Metadata() {
    var first = BuildMpeg1Layer3Frame(payloadSeed: 0x11);
    var second = BuildMpeg1Layer3Frame(payloadSeed: 0x37);
    byte[] inputBytes = [.. BuildEmptyId3v2Tag(), .. first, .. second];
    byte[] expected = [.. first, .. second];

    using var input = new MemoryStream(inputBytes, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new Mp3FormatDescriptor(),
      output,
      new Mp3FormatDescriptor(),
      new FormatCreateOptions(Method: "mp3"));

    Assert.That(output.ToArray(), Is.EqualTo(expected));

    using var packetInput = new MemoryStream(inputBytes, writable: false);
    Assert.That(Mp3AudioPacketAdapter.Instance.TryDemux(packetInput, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(encoded!.Format.CodecId, Is.EqualTo("mp3"));
      Assert.That(encoded.Format.SampleRate, Is.EqualTo(44_100));
      Assert.That(encoded.Format.Channels, Is.EqualTo(2));
      Assert.That(encoded.Packets.Count, Is.EqualTo(2));
      Assert.That(encoded.Packets.All(static packet => packet.DurationSamples == 1_152), Is.True);
      Assert.That(encoded.Packets[0].Data, Is.EqualTo(first));
      Assert.That(encoded.Packets[1].Data, Is.EqualTo(second));
    });
  }

  [Test]
  public void Mp3Demux_RejectsTruncatedFrame() {
    var frame = BuildMpeg1Layer3Frame();
    using var input = new MemoryStream(frame[..^1], writable: false);

    var exception = Assert.Throws<InvalidDataException>(() =>
      Mp3AudioPacketAdapter.Instance.TryDemux(input, out _));

    Assert.That(exception!.Message, Does.Contain("Truncated MPEG audio frame"));
  }

  [Test]
  public void Mp3Demux_RejectsGeometryTransition() {
    var first = BuildMpeg1Layer3Frame(sampleRateIndex: 0);
    var second = BuildMpeg1Layer3Frame(sampleRateIndex: 1);
    byte[] bytes = [.. first, .. second];
    using var input = new MemoryStream(bytes, writable: false);

    var exception = Assert.Throws<InvalidDataException>(() =>
      Mp3AudioPacketAdapter.Instance.TryDemux(input, out _));

    Assert.That(exception!.Message, Does.Contain("changes version, layer, sample rate, or channel count"));
  }

  [Test]
  public void Mp3Demux_RejectsFreeFormatFrames() {
    var freeFormatHeader = new byte[] { 0xFF, 0xFB, 0x00, 0x00 };
    using var input = new MemoryStream(freeFormatHeader, writable: false);

    Assert.Throws<NotSupportedException>(() =>
      Mp3AudioPacketAdapter.Instance.TryDemux(input, out _));
  }

  [Test]
  public void WavPackResolverRoute_PreservesPhysicalBlocks() {
    var first = BuildWavPackBlock(blockIndex: 0, blockSamples: 100, totalSamples: 220);
    var second = BuildWavPackBlock(blockIndex: 100, blockSamples: 120, totalSamples: 220);
    byte[] bytes = [.. first, .. second];

    using var input = new MemoryStream(bytes, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavPackFormatDescriptor(),
      output,
      new WavPackFormatDescriptor(),
      new FormatCreateOptions(Method: "wavpack"));

    Assert.That(output.ToArray(), Is.EqualTo(bytes));

    using var packetInput = new MemoryStream(bytes, writable: false);
    Assert.That(WavPackAudioPacketAdapter.Instance.TryDemux(packetInput, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(encoded!.Format.CodecId, Is.EqualTo("wavpack"));
      Assert.That(encoded.Format.SampleRate, Is.EqualTo(44_100));
      Assert.That(encoded.Format.Channels, Is.EqualTo(2));
      Assert.That(encoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(encoded.Packets.Count, Is.EqualTo(2));
      Assert.That(encoded.Packets[0].DurationSamples, Is.EqualTo(100));
      Assert.That(encoded.Packets[0].GranulePosition, Is.EqualTo(100));
      Assert.That(encoded.Packets[1].DurationSamples, Is.EqualTo(120));
      Assert.That(encoded.Packets[1].GranulePosition, Is.EqualTo(220));
    });
  }

  [Test]
  public void WavPackDemux_PreservesVersion5FortyBitBlockIndex() {
    const ulong blockIndex = (1UL << 32) + 7;
    var block = BuildWavPackBlock(blockIndex, blockSamples: 3, totalSamples: 0xFFFF_FFFF);
    using var input = new MemoryStream(block, writable: false);

    Assert.That(WavPackAudioPacketAdapter.Instance.TryDemux(input, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.That(encoded!.Packets.Single().GranulePosition, Is.EqualTo((long)blockIndex + 3));
  }

  [Test]
  public void WavPackDemux_RejectsTruncatedBlock() {
    var block = BuildWavPackBlock(blockIndex: 0, blockSamples: 100, totalSamples: 100);
    using var input = new MemoryStream(block[..^1], writable: false);

    var exception = Assert.Throws<InvalidDataException>(() =>
      WavPackAudioPacketAdapter.Instance.TryDemux(input, out _));

    Assert.That(exception!.Message, Does.Contain("Truncated WavPack block"));
  }

  [Test]
  public void WavPackDemux_RejectsUnsupportedBlockVersion() {
    var block = BuildWavPackBlock(blockIndex: 0, blockSamples: 100, totalSamples: 100);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0x0401);
    using var input = new MemoryStream(block, writable: false);

    Assert.Throws<InvalidDataException>(() =>
      WavPackAudioPacketAdapter.Instance.TryDemux(input, out _));
  }

  [TestCase(typeof(Mp3FormatDescriptor), "mp3")]
  [TestCase(typeof(WavPackFormatDescriptor), "wavpack")]
  public void ResolverBackedPacketCapabilities_AreReported(Type descriptorType, string codec) {
    var descriptor = (IFormatDescriptor)Activator.CreateInstance(descriptorType)!;
    var capability = AudioConversionInventory.Describe(descriptor);

    Assert.Multiple(() => {
      Assert.That(capability.CanDemuxEncoded, Is.True);
      Assert.That(capability.CanMuxEncoded, Is.True);
      Assert.That(capability.MuxCodecs, Does.Contain(codec));
    });
  }

  [Test]
  public void GenericArchiveCreateCapability_IsNotAnAudioSink() {
    var target = new GenericArchiveTarget();
    var capability = AudioConversionInventory.Describe(target);
    Assert.Multiple(() => {
      Assert.That(capability.CanCreatePseudoArchive, Is.False);
      Assert.That(capability.CanBeTarget, Is.False);
    });

    var wav = PcmCodec.ToWavBlob(new byte[128], numChannels: 1, sampleRate: 8_000, bitsPerSample: 16);
    using var input = new MemoryStream(wav, writable: false);
    using var output = new MemoryStream();

    Assert.Throws<NotSupportedException>(() =>
      AudioConversionOperation.Convert(input, new WavFormatDescriptor(), output, target));
    Assert.That(target.CreateCalled, Is.False);
  }

  private static byte[] BuildMpeg1Layer3Frame(int sampleRateIndex = 0, byte payloadSeed = 0x5A) {
    int[] bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    int[] sampleRates = [44_100, 48_000, 32_000];
    const int bitrateIndex = 9;
    var frameSize = 144 * bitrates[bitrateIndex] * 1000 / sampleRates[sampleRateIndex];
    var frame = new byte[frameSize];
    var header = 0xFFE0_0000u |
                 3u << 19 |
                 1u << 17 |
                 1u << 16 |
                 (uint)bitrateIndex << 12 |
                 (uint)sampleRateIndex << 10;
    BinaryPrimitives.WriteUInt32BigEndian(frame, header);
    for (var i = 4; i < frame.Length; ++i)
      frame[i] = (byte)(payloadSeed + i * 17);
    return frame;
  }

  private static byte[] BuildEmptyId3v2Tag()
    => [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

  private static byte[] BuildWavPackBlock(ulong blockIndex, uint blockSamples, uint totalSamples) {
    const uint flags = 1u | (9u << 23) | 0x800u | 0x1000u;
    var block = new byte[32];
    "wvpk"u8.CopyTo(block);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 24);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0x0410);
    block[10] = checked((byte)(blockIndex >> 32));
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), totalSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)blockIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), blockSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), flags);
    return block;
  }

  private sealed class GenericArchiveTarget : IFormatDescriptor, IArchiveCreatable {
    public bool CreateCalled { get; private set; }
    public string Id => "GenericArchiveTarget";
    public string DisplayName => "Generic archive target";
    public FormatCategory Category => FormatCategory.Archive;
    public FormatCapabilities Capabilities => FormatCapabilities.CanCreate;
    public string DefaultExtension => ".fake";
    public IReadOnlyList<string> Extensions => [this.DefaultExtension];
    public IReadOnlyList<string> CompoundExtensions => [];
    public IReadOnlyList<MagicSignature> MagicSignatures => [];
    public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    public string? TarCompressionFormatId => null;

    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
      => this.CreateCalled = true;
  }
}
