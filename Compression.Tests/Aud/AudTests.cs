#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Codec.WsAdpcm;
using Compression.Registry;
using FileFormat.Aud;

namespace Compression.Tests.Aud;

[TestFixture]
public class AudTests {

  private const uint ChunkMagic = 0x0000DEAF;

  // Builds an AUD file with one WS-ADPCM chunk over a raw 8-bit payload.
  private static byte[] BuildWsAud(byte[] wsPayload, int outSize, int sampleRate) {
    using var ms = new MemoryStream();
    var header = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), (ushort)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), (uint)wsPayload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6), (uint)outSize);
    header[10] = 0x02; // 16-bit, mono
    header[11] = 1;    // WS-ADPCM
    ms.Write(header);

    var chunk = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(0), (ushort)wsPayload.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(2), (ushort)outSize);
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), ChunkMagic);
    ms.Write(chunk);
    ms.Write(wsPayload);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    // Raw WS chunk: inSize == outSize → verbatim copy of 4 bytes.
    var aud = BuildWsAud([128, 130, 126, 200], outSize: 4, sampleRate: 22050);
    using var ms = new MemoryStream(aud);
    var entries = new AudFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.aud"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "FULL.aud").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_WsAdpcmRawChunk_DecodesToExpectedPcm() {
    var samples8 = new byte[] { 128, 129, 127, 255 };
    var aud = BuildWsAud(samples8, outSize: samples8.Length, sampleRate: 16000);
    using var ms = new MemoryStream(aud);
    using var output = new MemoryStream();
    new AudFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    var expected = WsAdpcmCodec.ToPcm16(samples8);
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)), Is.EqualTo(expected[i]));
  }

  [Test]
  public void ValidateHeader_RejectsBadRateAndCodec() {
    var d = new AudFormatDescriptor();

    var badRate = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(badRate.AsSpan(0), 100); // too low
    badRate[11] = 1;
    Assert.That(d.ValidateHeader(badRate, badRate.Length).IsValid, Is.False);

    var badCodec = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(badCodec.AsSpan(0), 22050);
    badCodec[11] = 42;
    Assert.That(d.ValidateHeader(badCodec, badCodec.Length).IsValid, Is.False);

    var ok = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(ok.AsSpan(0), 22050);
    ok[11] = 99;
    Assert.That(d.ValidateHeader(ok, ok.Length).IsValid, Is.True);
  }

  [Test]
  public void Descriptor_Create_FromMonoWav_RoundTripsThroughReader() {
    const int n = 256;
    var pcm = new byte[n * 2];
    for (var i = 0; i < n; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 7.0) * 7000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 22050, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new AudFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var aud = created.ToArray();

    // codec must be 99 (IMA) and chunk magic 0xDEAF present.
    Assert.That(aud[11], Is.EqualTo(99));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(aud.AsSpan(12 + 4)), Is.EqualTo(ChunkMagic));

    // Re-open and confirm the channel survives within IMA tolerance.
    using var reopen = new MemoryStream(aud);
    using var monoOut = new MemoryStream();
    new AudFormatDescriptor().ExtractEntry(reopen, "MONO.wav", monoOut, null);
    var mono = monoOut.ToArray();
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(n * 2)));

    double err = 0;
    for (var i = 0; i < n; ++i) {
      var got = BinaryPrimitives.ReadInt16LittleEndian(mono.AsSpan(44 + i * 2));
      var want = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
      err += Math.Abs(got - want);
    }
    err /= n;
    Assert.That(err, Is.LessThan(500.0));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullAud() {
    var original = BuildWsAud([128, 64, 192], outSize: 3, sampleRate: 8000);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.aud", original) };
    using var output = new MemoryStream();
    new AudFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Descriptor_Stereo_SurfacesLeftAndRightChannels() {
    const int frames = 128;
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * 2) * 2), (short)(Math.Sin(i / 5.0) * 5000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * 2 + 1) * 2), (short)(Math.Cos(i / 5.0) * 5000));
    }
    var wav = PcmCodec.ToWavBlob(pcm, channels: 2, sampleRate: 22050, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("stereo.wav", wav) };
    using var created = new MemoryStream();
    new AudFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    using var reopen = new MemoryStream(created.ToArray());
    var entries = new AudFormatDescriptor().List(reopen, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  [Test]
  public void Descriptor_BadChunkMagic_Throws() {
    var aud = BuildWsAud([1, 2, 3], outSize: 3, sampleRate: 22050);
    BinaryPrimitives.WriteUInt32LittleEndian(aud.AsSpan(12 + 4), 0x12345678); // corrupt magic
    using var ms = new MemoryStream(aud);
    Assert.That(() => new AudFormatDescriptor().List(ms, null), Throws.Exception);
  }
}
