#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
public sealed class AsfWriteSafetyTests {
  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  [Test]
  public void Add_RawReplacementWithDifferentLength_DropsStaleObjectBoundaryLedger() {
    var descriptor = new AsfFormatDescriptor();
    using var archive = CreatePcmArchive(descriptor, Pcm(160));
    var original = Extract(descriptor, archive, "streams/stream_01.bin");
    var replacement = new byte[original.Length + 2];
    original.CopyTo(replacement, 0);
    replacement[^2] = 0x34;
    replacement[^1] = 0x12;

    descriptor.Add(archive, [ArchiveInputInfo.InMemory("streams/stream_01.bin", replacement)]);

    Assert.That(Extract(descriptor, archive, "streams/stream_01.bin"), Is.EqualTo(replacement));
  }

  [Test]
  public void Add_RootWav_ReplacesExistingStreamInsteadOfBeingShadowedByCanonicalRawEntry() {
    var descriptor = new AsfFormatDescriptor();
    using var archive = CreatePcmArchive(descriptor, Pcm(160, seed: 1));
    var replacementPcm = Pcm(160, seed: 97);
    var replacementWav = PcmCodec.ToWavBlob(replacementPcm, 1, 8000, 16);

    descriptor.Add(archive, [ArchiveInputInfo.InMemory("MONO.wav", replacementWav)]);

    Assert.That(Extract(descriptor, archive, "streams/stream_01.bin"), Is.EqualTo(replacementPcm));
  }

  /// <summary>
  /// Header Object children the audio route authors itself — the empty Header Extension and the
  /// Content Description — must not divert an audio container to the byte-preserving container
  /// route: that route cannot map a WAV input at all, so treating a mere title as unreproducible
  /// would turn a working WAV replacement into a refusal.
  /// </summary>
  [Test]
  public void Add_RootWav_ToTaggedAudioContainer_StillTakesTheAudioRoute() {
    var descriptor = new AsfFormatDescriptor();
    var wav = PcmCodec.ToWavBlob(Pcm(160, seed: 3), 1, 8000, 16);
    var archive = new MemoryStream();
    descriptor.Create(archive, [
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes("[ContentDescription]\ntitle = routed\n")),
      ArchiveInputInfo.InMemory("MONO.wav", wav),
    ], new FormatCreateOptions());
    archive.Position = 0;

    var replacementPcm = Pcm(160, seed: 41);
    descriptor.Add(archive, [ArchiveInputInfo.InMemory("MONO.wav", PcmCodec.ToWavBlob(replacementPcm, 1, 8000, 16))]);

    Assert.Multiple(() => {
      Assert.That(Extract(descriptor, archive, "streams/stream_01.bin"), Is.EqualTo(replacementPcm));
      Assert.That(Encoding.UTF8.GetString(Extract(descriptor, archive, "metadata.ini")), Does.Contain("title = routed"));
    });
  }

  /// <summary>
  /// A video container is remuxable now that the codec-preserving container route exists, so the
  /// refusal this pins is no longer "the stream is video". What still fails closed is a container
  /// with no readable Data Object: there is no packet region to carry forward, and rebuilding it
  /// would hand the caller a structurally complete file whose media had silently vanished.
  /// </summary>
  [Test]
  public void Edit_AsfWithoutDataObject_FailsClosedAndLeavesOriginalBytesUntouched() {
    var descriptor = new AsfFormatDescriptor();
    var original = BuildVideoOnlyHeader();
    using var archive = new MemoryStream((byte[])original.Clone(), writable: true);

    Assert.That(
      () => descriptor.Add(archive, []),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("Data Object"));
    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

  private static MemoryStream CreatePcmArchive(AsfFormatDescriptor descriptor, byte[] pcm) {
    var wav = PcmCodec.ToWavBlob(pcm, 1, 8000, 16);
    var archive = new MemoryStream();
    descriptor.Create(archive, [ArchiveInputInfo.InMemory("MONO.wav", wav)], new FormatCreateOptions());
    archive.Position = 0;
    return archive;
  }

  private static byte[] Extract(AsfFormatDescriptor descriptor, MemoryStream archive, string name) {
    archive.Position = 0;
    using var output = new MemoryStream();
    descriptor.ExtractEntry(archive, name, output, null);
    archive.Position = 0;
    return output.ToArray();
  }

  private static byte[] Pcm(int samples, int seed = 0) {
    var result = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), unchecked((short)(seed + i * 257)));
    return result;
  }

  private static byte[] BuildVideoOnlyHeader() {
    using var streamPropertiesBody = new MemoryStream();
    streamPropertiesBody.Write(VideoStreamType);
    streamPropertiesBody.Write(new byte[16]);
    WriteU64(streamPropertiesBody, 0);
    WriteU32(streamPropertiesBody, 0);
    WriteU32(streamPropertiesBody, 0);
    WriteU16(streamPropertiesBody, 1);
    WriteU32(streamPropertiesBody, 0);
    var streamProperties = WrapObject(StreamPropertiesObject, streamPropertiesBody.ToArray());

    using var header = new MemoryStream();
    header.Write(HeaderObject);
    WriteU64(header, checked((ulong)(30 + streamProperties.Length)));
    WriteU32(header, 1);
    header.WriteByte(1);
    header.WriteByte(2);
    header.Write(streamProperties);
    return header.ToArray();
  }

  private static byte[] WrapObject(byte[] guid, byte[] body) {
    using var output = new MemoryStream();
    output.Write(guid);
    WriteU64(output, checked((ulong)(24 + body.Length)));
    output.Write(body);
    return output.ToArray();
  }

  private static void WriteU16(Stream stream, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteU32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteU64(Stream stream, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }
}
