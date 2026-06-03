#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Ircam;

namespace Compression.Tests.Ircam;

[TestFixture]
public class IrcamTests {

  private const int DataOffset = 1024;

  private static byte[] BuildHeader(bool littleEndian, float sampleRate, uint channels, uint format) {
    var hdr = new byte[DataOffset];
    if (littleEndian) {
      hdr[0] = 0x64; hdr[1] = 0xA3; hdr[2] = 0x01; hdr[3] = 0x00;
      BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(4), sampleRate);
      BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), channels);
      BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), format);
    } else {
      hdr[0] = 0x00; hdr[1] = 0x01; hdr[2] = 0xA3; hdr[3] = 0x64;
      BinaryPrimitives.WriteSingleBigEndian(hdr.AsSpan(4), sampleRate);
      BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8), channels);
      BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(12), format);
    }
    return hdr;
  }

  private static byte[] MakeIrcam(bool littleEndian, float sampleRate, uint channels, uint format, byte[] data) {
    var hdr = BuildHeader(littleEndian, sampleRate, channels, format);
    var blob = new byte[DataOffset + data.Length];
    hdr.CopyTo(blob, 0);
    data.CopyTo(blob, DataOffset);
    return blob;
  }

  // 16-bit stereo little-endian PCM, 6 frames.
  private static byte[] StereoLe16() {
    var data = new byte[6 * 2 * 2];
    for (var i = 0; i < 6; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4), (short)(i * 150));
      BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4 + 2), (short)(i * -150));
    }
    return MakeIrcam(true, 44100f, 2, 2, data);
  }

  [Test]
  public void List_StereoLe16_SurfacesFullAndChannels() {
    using var ms = new MemoryStream(StereoLe16());
    var entries = new IrcamFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sf" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void ExtractEntry_Channel_IsValidMonoRiff() {
    using var ms = new MemoryStream(StereoLe16());
    using var output = new MemoryStream();
    new IrcamFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)), Is.EqualTo(44100u));
  }

  [Test]
  public void BigEndian16_IsByteSwappedToLittleEndian() {
    var values = new short[] { 0x0102, -200, 12000, 0x7F00 };
    var be = new byte[values.Length * 2];
    for (var i = 0; i < values.Length; ++i)
      BinaryPrimitives.WriteInt16BigEndian(be.AsSpan(i * 2), values[i]);
    var blob = MakeIrcam(false, 16000f, 1, 2, be);

    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new IrcamFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var pcm = output.ToArray().AsSpan(44);
    for (var i = 0; i < values.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]), Is.EqualTo(values[i]));
  }

  [Test]
  public void Float32_SurfacesFloatChannels() {
    // Mono 32-bit float, big-endian on disk.
    var values = new float[] { 0.0f, 0.5f, -0.25f, 1.0f };
    var be = new byte[values.Length * 4];
    for (var i = 0; i < values.Length; ++i)
      BinaryPrimitives.WriteSingleBigEndian(be.AsSpan(i * 4), values[i]);
    var blob = MakeIrcam(false, 48000f, 1, 4, be);

    using var ms = new MemoryStream(blob);
    var entries = new IrcamFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);

    using var ms2 = new MemoryStream(blob);
    using var output = new MemoryStream();
    new IrcamFormatDescriptor().ExtractEntry(ms2, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)), Is.EqualTo(3)); // IEEE float
    var pcm = wav.AsSpan(44);
    for (var i = 0; i < values.Length; ++i)
      Assert.That(BinaryPrimitives.ReadSingleLittleEndian(pcm[(i * 4)..]), Is.EqualTo(values[i]));
  }

  [Test]
  public void UnsupportedFormat_IsFullOnly() {
    var blob = MakeIrcam(true, 16000f, 1, 99, new byte[16]);
    using var ms = new MemoryStream(blob);
    var entries = new IrcamFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sf"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void Create_RoundTripsThroughReader() {
    var left = new byte[5 * 2];
    var right = new byte[5 * 2];
    for (var i = 0; i < 5; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 321));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(i * -123));
    }
    var leftWav = PcmCodec.ToWavBlob(left, 1, 32000, 16);
    var rightWav = PcmCodec.ToWavBlob(right, 1, 32000, 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav),
    };

    using var created = new MemoryStream();
    new IrcamFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var blob = created.ToArray();

    using var read = new MemoryStream(blob);
    var entries = new IrcamFormatDescriptor().List(read, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);

    using var read2 = new MemoryStream(blob);
    using var outRight = new MemoryStream();
    new IrcamFormatDescriptor().ExtractEntry(read2, "RIGHT.wav", outRight, null);
    Assert.That(outRight.ToArray().AsSpan(44).ToArray(), Is.EqualTo(right));
  }
}
