#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.ALaw;
using Codec.MuLaw;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
public sealed class AsfTrackWriterTests {
  [Test]
  public void Descriptor_AdvertisesCreateModifyAndRejectsUnrelatedInputs() {
    var descriptor = new AsfFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor.CanPurgeToEmpty, Is.False);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("MONO.wav", []), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("streams/stream_07.bin", []), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("notes.txt", []), out var reason), Is.False);
      Assert.That(reason, Does.Contain("not an ASF"));
    });
  }

  [Test]
  public void Create_FromStereoPcmWav_DemuxesOriginalPcmAndParameters() {
    var pcm = GeneratePcm16(channels: 2, frames: 4096);
    var wav = PcmCodec.ToWavBlob(pcm, channels: 2, sampleRate: 22050, bitsPerSample: 16);
    var asf = Create([ArchiveInputInfo.InMemory("stereo.wav", wav)]);

    var raw = Extract(asf, "streams/stream_01.bin");
    var info = Encoding.UTF8.GetString(Extract(asf, "streams/stream_01.info.txt"));
    Assert.Multiple(() => {
      Assert.That(raw, Is.EqualTo(pcm));
      Assert.That(info, Does.Contain("format_tag = 0x0001"));
      Assert.That(info, Does.Contain("channels = 2"));
      Assert.That(info, Does.Contain("sample_rate = 22050"));
      Assert.That(info, Does.Contain("byte_rate = 88200"));
      Assert.That(info, Does.Contain("block_align = 4"));
      Assert.That(info, Does.Contain("object_sizes = "));
    });
  }

  [TestCase(100)]
  [TestCase(3200)]
  [TestCase(65536)]
  public void Create_PacketSizeExtremes_FragmentAndRoundTrip(int packetSize) {
    var pcm = GeneratePcm16(channels: 1, frames: 70000);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 8000, 16);
    var metadata = Encoding.UTF8.GetBytes($"[FileProperties]\nmax_packet_size = {packetSize}\n");
    var asf = Create([
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
      ArchiveInputInfo.InMemory("MONO.wav", wav),
    ]);

    Assert.That(Extract(asf, "streams/stream_01.bin"), Is.EqualTo(pcm));
    var rendered = Encoding.UTF8.GetString(Extract(asf, "metadata.ini"));
    Assert.That(rendered, Does.Contain($"max_packet_size = {packetSize}"));
  }

  [TestCase(0x0006)]
  [TestCase(0x0007)]
  public void Create_G711FromPcm_UsesManagedEncoder(int formatTag) {
    const int frames = 257;
    var pcm = GeneratePcm16(1, frames);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 8000, 16);
    var info = Encoding.UTF8.GetBytes($"""
      stream_number = 1
      type = audio
      format_tag = 0x{formatTag:X4}
      channels = 1
      sample_rate = 8000
      bits_per_sample = 16
      encrypted = false
      """);
    var asf = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.info.txt", info),
      ArchiveInputInfo.InMemory("streams/stream_01/MONO.wav", wav),
    ]);

    var shorts = new short[pcm.Length / 2];
    for (var i = 0; i < shorts.Length; ++i)
      shorts[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    var expected = formatTag == 0x0006 ? ALawCodec.Encode(shorts) : MuLawCodec.Encode(shorts);
    Assert.That(Extract(asf, "streams/stream_01.bin"), Is.EqualTo(expected));

    var rendered = Encoding.UTF8.GetString(Extract(asf, "streams/stream_01.info.txt"));
    Assert.Multiple(() => {
      Assert.That(rendered, Does.Contain($"format_tag = 0x{formatTag:X4}"));
      Assert.That(rendered, Does.Contain("bits_per_sample = 8"));
      Assert.That(rendered, Does.Contain("byte_rate = 8000"));
      Assert.That(rendered, Does.Contain("block_align = 1"));
    });
  }

  [Test]
  public void Create_RawWma_RemuxesCodecPrivateDataAndObjectBoundaries() {
    var payload = Enumerable.Range(0, 23).Select(static i => (byte)(i * 7)).ToArray();
    var info = Encoding.UTF8.GetBytes("""
      stream_number = 3
      type = audio
      codec = wmav2
      format_tag = 0x0161
      channels = 2
      sample_rate = 44100
      bits_per_sample = 16
      bitrate = 128000
      byte_rate = 16000
      block_align = 5
      encrypted = false
      extra_data_hex = 010203040506
      object_sizes = 5,5,5,5,3
      """);
    var asf = Create([
      ArchiveInputInfo.InMemory("streams/stream_03.info.txt", info),
      ArchiveInputInfo.InMemory("streams/stream_03.bin", payload),
    ]);

    Assert.That(Extract(asf, "streams/stream_03.bin"), Is.EqualTo(payload));
    var rendered = Encoding.UTF8.GetString(Extract(asf, "streams/stream_03.info.txt"));
    Assert.Multiple(() => {
      Assert.That(rendered, Does.Contain("format_tag = 0x0161"));
      Assert.That(rendered, Does.Contain("extra_data_hex = 010203040506"));
      Assert.That(rendered, Does.Contain("object_sizes = 5,5,5,5,3"));
    });
  }

  [Test]
  public void Create_MultipleRawStreams_SupportsFullStreamNumberRange() {
    var one = Enumerable.Range(0, 1000).Select(static i => (byte)i).ToArray();
    var two = Enumerable.Range(0, 777).Select(static i => (byte)(255 - i)).ToArray();
    var asf = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.info.txt", RawInfo(1, 0x0055, 2, 44100, 16000, 1, 0)),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", one),
      ArchiveInputInfo.InMemory("streams/stream_127.info.txt", RawInfo(127, 0x2000, 6, 48000, 48000, 1, 0)),
      ArchiveInputInfo.InMemory("streams/stream_127.bin", two),
    ]);

    Assert.Multiple(() => {
      Assert.That(Extract(asf, "streams/stream_01.bin"), Is.EqualTo(one));
      Assert.That(Extract(asf, "streams/stream_127.bin"), Is.EqualTo(two));
    });
  }

  [Test]
  public void Add_ReplacesEncodedStreamByTransactionalRemux() {
    var original = Enumerable.Range(0, 401).Select(static i => (byte)i).ToArray();
    var replacement = Enumerable.Range(0, 913).Select(static i => (byte)(i * 13)).ToArray();
    var info = RawInfo(4, 0x0055, 2, 44100, 16000, 1, 0);
    var asf = Create([
      ArchiveInputInfo.InMemory("streams/stream_04.info.txt", info),
      ArchiveInputInfo.InMemory("streams/stream_04.bin", original),
    ]);
    using var editable = new MemoryStream(asf);
    var descriptor = new AsfFormatDescriptor();

    descriptor.Add(editable, [ArchiveInputInfo.InMemory("streams/stream_04.bin", replacement)]);

    Assert.That(Extract(editable.ToArray(), "streams/stream_04.bin"), Is.EqualTo(replacement));
  }

  [Test]
  public void Remove_DropsOneStreamButRejectsRemovingLast() {
    var asf = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.info.txt", RawInfo(1, 0x0055, 2, 44100, 16000, 1, 0)),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", [1, 2, 3]),
      ArchiveInputInfo.InMemory("streams/stream_02.info.txt", RawInfo(2, 0x0055, 1, 22050, 8000, 1, 0)),
      ArchiveInputInfo.InMemory("streams/stream_02.bin", [4, 5, 6]),
    ]);
    using var editable = new MemoryStream(asf);
    var descriptor = new AsfFormatDescriptor();

    descriptor.Remove(editable, ["streams/stream_01.bin"]);
    var names = descriptor.List(new MemoryStream(editable.ToArray()), null).Select(static entry => entry.Name).ToArray();
    Assert.Multiple(() => {
      Assert.That(names, Does.Not.Contain("streams/stream_01.bin"));
      Assert.That(names, Does.Contain("streams/stream_02.bin"));
    });
    Assert.Throws<NotSupportedException>(() => descriptor.Remove(editable, ["streams/stream_02.bin"]));
  }

  [Test]
  public void Create_FullAsfOnly_IsByteExactPassThrough() {
    var source = Create([ArchiveInputInfo.InMemory("MONO.wav", PcmCodec.ToWavBlob(GeneratePcm16(1, 16), 1, 8000, 16))]);
    var passThrough = Create([ArchiveInputInfo.InMemory("FULL.asf", source)]);
    Assert.That(passThrough, Is.EqualTo(source));
  }

  [Test]
  public void Create_WmaFromWav_RefusesSilentTranscode() {
    var wav = PcmCodec.ToWavBlob(GeneratePcm16(1, 32), 1, 8000, 16);
    var info = Encoding.UTF8.GetBytes("""
      stream_number = 1
      type = audio
      format_tag = 0x0161
      channels = 1
      sample_rate = 8000
      block_align = 16
      bits_per_sample = 16
      encrypted = false
      """);
    var descriptor = new AsfFormatDescriptor();
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => descriptor.Create(output, [
      ArchiveInputInfo.InMemory("streams/stream_01.info.txt", info),
      ArchiveInputInfo.InMemory("streams/stream_01/MONO.wav", wav),
    ], new FormatCreateOptions()));
  }

  [Test]
  public void Create_EncryptedRawStream_IsRejected() {
    var info = Encoding.UTF8.GetBytes("""
      stream_number = 1
      type = audio
      format_tag = 0x0161
      channels = 1
      sample_rate = 8000
      byte_rate = 1000
      block_align = 16
      encrypted = true
      """);
    var descriptor = new AsfFormatDescriptor();
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => descriptor.Create(output, [
      ArchiveInputInfo.InMemory("streams/stream_01.info.txt", info),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", [1, 2, 3]),
    ], new FormatCreateOptions()));
  }

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs) {
    using var output = new MemoryStream();
    new AsfFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    return output.ToArray();
  }

  private static byte[] Extract(byte[] asf, string name) {
    using var output = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(asf), name, output, null);
    return output.ToArray();
  }

  private static byte[] GeneratePcm16(int channels, int frames) {
    var result = new byte[checked(channels * frames * 2)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)((frame * 811 + channel * 7919) & 0xFFFF);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan((frame * channels + channel) * 2), sample);
      }
    return result;
  }

  private static byte[] RawInfo(int stream, int tag, int channels, int sampleRate, int byteRate, int blockAlign, int bits)
    => Encoding.UTF8.GetBytes($"""
      stream_number = {stream}
      type = audio
      format_tag = 0x{tag:X4}
      channels = {channels}
      sample_rate = {sampleRate}
      byte_rate = {byteRate}
      block_align = {blockAlign}
      bits_per_sample = {bits}
      encrypted = false
      """);
}
