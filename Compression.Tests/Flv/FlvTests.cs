using System.Buffers.Binary;
using System.Text;
using FileFormat.Flv;

namespace Compression.Tests.Flv;

[TestFixture]
public class FlvTests {

  private static byte[] Header(bool audio, bool video) => [
    (byte)'F', (byte)'L', (byte)'V', 0x01, (byte)((audio ? 0x04 : 0) | (video ? 0x01 : 0)),
    0x00, 0x00, 0x00, 0x09,
    0x00, 0x00, 0x00, 0x00, // PreviousTagSize0
  ];

  private static byte[] Tag(byte type, uint timestamp, byte[] body) {
    var tag = new byte[11 + body.Length + 4];
    tag[0] = type;
    tag[1] = (byte)(body.Length >> 16); tag[2] = (byte)(body.Length >> 8); tag[3] = (byte)body.Length;
    tag[4] = (byte)(timestamp >> 16); tag[5] = (byte)(timestamp >> 8); tag[6] = (byte)timestamp; tag[7] = (byte)(timestamp >> 24);
    body.CopyTo(tag, 11);
    BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(11 + body.Length), (uint)(11 + body.Length));
    return tag;
  }

  private static byte[] Concat(params byte[][] parts) {
    using var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p);
    return ms.ToArray();
  }

  private static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1E, 0xAB];
  private static readonly byte[] Pps = [0x68, 0xCE, 0x38, 0x80];
  private static readonly byte[] Nal1 = [0x65, 0x88, 0x84, 0x00, 0x11];
  private static readonly byte[] Nal2 = [0x41, 0x9A, 0x22];
  private static readonly byte[] AacFrame = [0x21, 0x10, 0x05, 0x00];
  private static readonly byte[] Mp3Frame = [0xFF, 0xFB, 0x90, 0x00, 0xAA, 0xBB];

  private static byte[] AvcConfigBody() => Concat(
    [0x17, 0x00, 0x00, 0x00, 0x00],           // keyframe + AVC, packet type 0, composition time
    [0x01, 0x42, 0x00, 0x1E, 0xFF, 0xE1],     // record: version, profile, compat, level, lengthSizeMinusOne=3, numSPS=1
    [(byte)(Sps.Length >> 8), (byte)Sps.Length], Sps,
    [0x01, (byte)(Pps.Length >> 8), (byte)Pps.Length], Pps);

  private static byte[] AvcNaluBody() {
    using var ms = new MemoryStream();
    ms.Write([0x17, 0x01, 0x00, 0x00, 0x00]);
    Span<byte> len = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)Nal1.Length); ms.Write(len); ms.Write(Nal1);
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)Nal2.Length); ms.Write(len); ms.Write(Nal2);
    return ms.ToArray();
  }

  /// <summary>AudioSpecificConfig: AAC LC (2), 44.1 kHz (index 4), stereo (2).</summary>
  private static byte[] AacConfigBody() => [0xAF, 0x00, 0x12, 0x10];
  private static byte[] AacRawBody() => Concat([0xAF, 0x01], AacFrame);

  private static byte[] OnMetaDataBody() {
    using var ms = new MemoryStream();
    void Str(string s) { var b = Encoding.UTF8.GetBytes(s); ms.WriteByte((byte)(b.Length >> 8)); ms.WriteByte((byte)b.Length); ms.Write(b); }
    void Num(double d) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, d); ms.WriteByte(0x00); ms.Write(b); }
    ms.WriteByte(0x02); Str("onMetaData");
    ms.WriteByte(0x08); ms.Write([0x00, 0x00, 0x00, 0x03]);
    Str("duration"); Num(12.5);
    Str("width"); Num(320);
    Str("encoder"); ms.WriteByte(0x02); Str("test");
    ms.Write([0x00, 0x00, 0x09]);
    return ms.ToArray();
  }

  private static byte[] BuildFile() => Concat(
    Header(audio: true, video: true),
    Tag(FlvReader.TagScript, 0, OnMetaDataBody()),
    Tag(FlvReader.TagVideo, 0, AvcConfigBody()),
    Tag(FlvReader.TagAudio, 0, AacConfigBody()),
    Tag(FlvReader.TagVideo, 40, AvcNaluBody()),
    Tag(FlvReader.TagAudio, 23, AacRawBody()));

  [Test, Category("HappyPath")]
  public void Read_ParsesHeaderTagsAndMetadata() {
    var flv = FlvReader.Read(BuildFile());

    Assert.That(flv.Version, Is.EqualTo(1));
    Assert.That(flv.HasAudioFlag, Is.True);
    Assert.That(flv.HasVideoFlag, Is.True);
    Assert.That(flv.TagCount, Is.EqualTo(5));
    Assert.That(flv.LastTimestampMs, Is.EqualTo(40u));
    Assert.That(flv.Scripts, Has.Count.EqualTo(1));
    Assert.That(flv.Scripts[0].Name, Is.EqualTo("onMetaData"));
    Assert.That(flv.Metadata["duration"], Is.EqualTo("12.5"));
    Assert.That(flv.Metadata["width"], Is.EqualTo("320"));
    Assert.That(flv.Metadata["encoder"], Is.EqualTo("test"));
  }

  [Test, Category("HappyPath")]
  public void Read_AvcBecomesAnnexB() {
    var flv = FlvReader.Read(BuildFile());
    var video = flv.Streams.Single(s => s.Kind == "video");

    Assert.That(video.Codec, Is.EqualTo("h264"));
    Assert.That(video.EntryName, Is.EqualTo("video_h264.h264"));
    byte[] start = [0, 0, 0, 1];
    Assert.That(video.Payload, Is.EqualTo(Concat(start, Sps, start, Pps, start, Nal1, start, Nal2)));
    Assert.That(video.TagCount, Is.EqualTo(1), "the sequence header is not a picture tag");
    Assert.That(video.FirstTimestampMs, Is.EqualTo(40u));
  }

  [Test, Category("HappyPath")]
  public void Read_AacBecomesAdts() {
    var flv = FlvReader.Read(BuildFile());
    var audio = flv.Streams.Single(s => s.Kind == "audio");

    Assert.That(audio.EntryName, Is.EqualTo("audio_aac.aac"));
    var adts = audio.Payload;
    Assert.That(adts, Has.Length.EqualTo(7 + AacFrame.Length));
    Assert.That(adts[0], Is.EqualTo(0xFF));
    Assert.That(adts[1] & 0xF6, Is.EqualTo(0xF0));
    Assert.That((adts[2] >> 6), Is.EqualTo(1), "AAC LC profile");
    Assert.That((adts[2] >> 2) & 0x0F, Is.EqualTo(4), "44.1 kHz sampling index");
    Assert.That(((adts[2] & 1) << 2) | (adts[3] >> 6), Is.EqualTo(2), "stereo");
    var frameLength = ((adts[3] & 0x03) << 11) | (adts[4] << 3) | (adts[5] >> 5);
    Assert.That(frameLength, Is.EqualTo(7 + AacFrame.Length));
    Assert.That(adts.AsSpan(7).ToArray(), Is.EqualTo(AacFrame));
  }

  [Test, Category("HappyPath")]
  public void Read_Mp3AndVp6_AreConcatenatedRaw() {
    var vp6Body = new byte[] { 0x14, 0x00, 0xDE, 0xAD }; // keyframe + VP6, adjustment byte, two data bytes
    var file = Concat(
      Header(audio: true, video: true),
      Tag(FlvReader.TagAudio, 0, Concat([0x2F], Mp3Frame)),
      Tag(FlvReader.TagAudio, 26, Concat([0x2F], Mp3Frame)),
      Tag(FlvReader.TagVideo, 0, vp6Body));
    var flv = FlvReader.Read(file);

    var mp3 = flv.Streams.Single(s => s.Kind == "audio");
    Assert.That(mp3.EntryName, Is.EqualTo("audio_mp3.mp3"));
    Assert.That(mp3.Payload, Is.EqualTo(Concat(Mp3Frame, Mp3Frame)));
    Assert.That(mp3.TagCount, Is.EqualTo(2));
    var vp6 = flv.Streams.Single(s => s.Kind == "video");
    Assert.That(vp6.EntryName, Is.EqualTo("video_vp6.bin"));
    Assert.That(vp6.Payload, Is.EqualTo(new byte[] { 0xDE, 0xAD }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataScriptAndStreams() {
    using var ms = new MemoryStream(BuildFile());
    var entries = new FlvFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] {
      "metadata.ini", "script_000_onMetaData.amf", "audio_aac.aac", "video_h264.h264",
    }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesStreamsAndMetadata() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(BuildFile());
      new FlvFormatDescriptor().Extract(ms, tmp, null, null);
      var metadata = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(metadata, Does.Contain("tag_count = 5"));
      Assert.That(metadata, Does.Contain("[onMetaData]"));
      Assert.That(metadata, Does.Contain("duration = 12.5"));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "script_000_onMetaData.amf")), Is.EqualTo(OnMetaDataBody()));
      Assert.That(File.Exists(Path.Combine(tmp, "video_h264.h264")), Is.True);
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractEntry_StreamsOneEntry() {
    using var ms = new MemoryStream(BuildFile());
    using var output = new MemoryStream();
    new FlvFormatDescriptor().ExtractEntry(ms, "audio_aac.aac", output, null);
    Assert.That(output.Length, Is.EqualTo(7 + AacFrame.Length));
  }

  [Test, Category("ErrorHandling")]
  public void Read_WithoutSignature_Throws() {
    Assert.Throws<InvalidDataException>(() => FlvReader.Read([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]));
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedTag_KeepsCompleteTags() {
    var full = BuildFile();
    var flv = FlvReader.Read(full.AsSpan(0, full.Length - 8));
    Assert.That(flv.TagCount, Is.EqualTo(4));
    Assert.That(flv.Streams.Any(s => s.Kind == "video"), Is.True);
  }
}
