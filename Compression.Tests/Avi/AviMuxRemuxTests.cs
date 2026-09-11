#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Avi;

namespace Compression.Tests.Avi;

[TestFixture]
public sealed class AviMuxRemuxTests {

  [Test, Category("HappyPath")]
  public void DescriptorAdvertisesCreateAndImplementsCreator() {
    var descriptor = new AviFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    });
  }

  [Test, Category("RoundTrip")]
  public void RemuxPreservesInterleavePacketsAndLegacyIndexFlags() {
    var source = BuildInterleavedAvi();
    using var remuxed = new MemoryStream();

    ((IArchiveCreatable)new AviFormatDescriptor()).Create(
      remuxed,
      [ArchiveInputInfo.InMemory("FULL.avi", source)],
      new FormatCreateOptions());

    var parsed = new AviReader().Read(remuxed.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.Tracks.Count, Is.EqualTo(2));
      Assert.That(parsed.MoviChunks.Select(static chunk => chunk.ChunkId),
        Is.EqualTo(new[] { "00dc", "01wb", "00dc", "01wb" }));
      Assert.That(parsed.MoviChunks.Select(static chunk => chunk.Data),
        Is.EqualTo(new[] {
          "video-1"u8.ToArray(), [1, 2, 3, 4], "video-2"u8.ToArray(), [5, 6, 7, 8],
        }));
      Assert.That(parsed.MoviChunks.Select(static chunk => chunk.IndexFlags),
        Is.EqualTo(new uint?[] { 0x10, 0x10, 0, 0x10 }));
      Assert.That(parsed.Tracks[0].StreamHeader, Is.Not.Empty);
      Assert.That(parsed.MainHeader, Is.Not.Empty);
    });

    AssertCanonicalTopLevelOrder(remuxed.ToArray());
  }

  [Test, Category("RoundTrip")]
  public void ElementaryMuxAcceptsExtractedHexPcmMetadata() {
    var pcm = new byte[] { 0, 0, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0 };
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 8000, bitsPerSample: 16);
    var metadata = Encoding.UTF8.GetBytes(
      "width=2\n" +
      "height=2\n" +
      "microseconds_per_frame=40000\n" +
      "total_frames=2\n" +
      "track_count=2\n" +
      "track_0.type=vids\n" +
      "track_0.fourcc=MJPG\n" +
      "track_0.width=2\n" +
      "track_0.height=2\n" +
      "track_0.frame_count=2\n" +
      "track_1.type=auds\n" +
      "track_1.fourcc=????\n" +
      "track_1.channels=1\n" +
      "track_1.sample_rate=8000\n" +
      "track_1.bits_per_sample=16\n" +
      "track_1.format_tag=0x0001\n");

    using var output = new MemoryStream();
    ((IArchiveCreatable)new AviFormatDescriptor()).Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
      ArchiveInputInfo.InMemory("frames/track_00/frame_000001.jpg", "jpeg-one"u8.ToArray()),
      ArchiveInputInfo.InMemory("frames/track_00/frame_000002.jpg", "jpeg-two"u8.ToArray()),
      ArchiveInputInfo.InMemory("track_01_audio.wav", wav),
    ], new FormatCreateOptions());

    var parsed = new AviReader().Read(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.Width, Is.EqualTo(2));
      Assert.That(parsed.Height, Is.EqualTo(2));
      Assert.That(parsed.TotalFrames, Is.EqualTo(2));
      Assert.That(parsed.Tracks.Count, Is.EqualTo(2));
      Assert.That(parsed.Tracks[0].Handler, Is.EqualTo(FourCc("MJPG")));
      Assert.That(parsed.Tracks[0].Chunks.Select(static chunk => chunk.Data),
        Is.EqualTo(new[] { "jpeg-one"u8.ToArray(), "jpeg-two"u8.ToArray() }));
      Assert.That(parsed.Tracks[1].AudioFormatTag, Is.EqualTo(1));
      Assert.That(parsed.Tracks[1].AudioSampleRate, Is.EqualTo(8000));
      Assert.That(parsed.Tracks[1].AudioChannels, Is.EqualTo(1));
      Assert.That(parsed.Tracks[1].AudioBitsPerSample, Is.EqualTo(16));
      Assert.That(parsed.Tracks[1].Data, Is.EqualTo(pcm));
    });

    AssertCanonicalTopLevelOrder(output.ToArray());
  }

  private static byte[] BuildInterleavedAvi() {
    var videoFormat = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(videoFormat.AsSpan(0), 40);
    BinaryPrimitives.WriteInt32LittleEndian(videoFormat.AsSpan(4), 16);
    BinaryPrimitives.WriteInt32LittleEndian(videoFormat.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(videoFormat.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(videoFormat.AsSpan(14), 24);
    BinaryPrimitives.WriteUInt32LittleEndian(videoFormat.AsSpan(16), FourCc("MJPG"));

    var audioFormat = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(audioFormat.AsSpan(0), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(audioFormat.AsSpan(2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(audioFormat.AsSpan(4), 8000);
    BinaryPrimitives.WriteUInt32LittleEndian(audioFormat.AsSpan(8), 16000);
    BinaryPrimitives.WriteUInt16LittleEndian(audioFormat.AsSpan(12), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(audioFormat.AsSpan(14), 16);

    var video1 = "video-1"u8.ToArray();
    var video2 = "video-2"u8.ToArray();
    byte[] audio1 = [1, 2, 3, 4];
    byte[] audio2 = [5, 6, 7, 8];

    var tracks = new AviReader.Track[] {
      new(0, "vids", FourCc("MJPG"), videoFormat, 16, 16, 0, 0, 0, 0, 0,
        [.. video1, .. video2], [new("00dc", video1), new("00dc", video2)]),
      new(1, "auds", 0, audioFormat, 0, 0, 1, 8000, 16, 1, 2,
        [.. audio1, .. audio2], [new("01wb", audio1), new("01wb", audio2)]),
    };

    var avi = new AviReader.ParsedAvi(16, 16, 40000, 2, tracks) {
      MoviChunks = [
        new(0, "00dc", video1, 0x10),
        new(1, "01wb", audio1, 0x10),
        new(0, "00dc", video2, 0),
        new(1, "01wb", audio2, 0x10),
      ],
    };

    using var output = new MemoryStream();
    AviWriter.Write(output, avi);
    return output.ToArray();
  }

  private static void AssertCanonicalTopLevelOrder(ReadOnlySpan<byte> avi) {
    Assert.That(avi[..4].SequenceEqual("RIFF"u8), Is.True);
    Assert.That(avi.Slice(8, 4).SequenceEqual("AVI "u8), Is.True);

    var ids = new List<string>();
    var offset = 12;
    while (offset + 8 <= avi.Length) {
      var id = Encoding.ASCII.GetString(avi.Slice(offset, 4));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(avi[(offset + 4)..]);
      if (size > int.MaxValue || offset + 8L + size > avi.Length)
        break;
      if (id == "LIST" && size >= 4)
        ids.Add("LIST/" + Encoding.ASCII.GetString(avi.Slice(offset + 8, 4)));
      else
        ids.Add(id);
      offset = checked(offset + 8 + (int)size + ((int)size & 1));
    }

    Assert.That(ids, Is.EqualTo(new[] { "LIST/hdrl", "LIST/movi", "idx1" }));
  }

  private static uint FourCc(string text)
    => (uint)(byte)text[0]
       | (uint)(byte)text[1] << 8
       | (uint)(byte)text[2] << 16
       | (uint)(byte)text[3] << 24;
}
