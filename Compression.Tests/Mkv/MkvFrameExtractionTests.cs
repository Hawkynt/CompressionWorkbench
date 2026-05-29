#pragma warning disable CS1591
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

[TestFixture]
public class MkvFrameExtractionTests {

  /// <summary>
  /// Builds a minimal MKV file with one video track (V_MPEG4/ISO/AVC) and N SimpleBlocks.
  /// Each block contains a small synthetic payload.
  /// </summary>
  private static byte[] MakeMkvWithVideoFrames(int frameCount, string codecId = "V_MPEG4/ISO/AVC") {
    var ms = new MemoryStream();

    // EBML Header
    var ebmlBody = BuildEbmlHeaderBody();
    WriteEbmlElement(ms, 0x1A45DFA3, ebmlBody);

    // Segment with unknown size
    WriteEbmlId(ms, 0x18538067);
    ms.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    // Tracks element (0x1654AE6B) with one TrackEntry
    var tracksBody = BuildTracksElement(1, "video", codecId);
    WriteEbmlElement(ms, 0x1654AE6B, tracksBody);

    // Cluster (0x1F43B675) with N SimpleBlocks
    var clusterBody = new MemoryStream();
    // Timecode (0xE7) = 0
    WriteEbmlElement(clusterBody, 0xE7, [0x00]);
    for (var i = 0; i < frameCount; ++i) {
      // SimpleBlock body: track number vint(1) + 2-byte timecode + 1-byte flags + payload
      var payload = new byte[] { 0xAA, 0xBB, (byte)(i + 1) };
      var blockBody = new byte[1 + 2 + 1 + payload.Length];
      blockBody[0] = 0x81; // track 1 as vint
      blockBody[1] = (byte)((i >> 8) & 0xFF); // timecode hi
      blockBody[2] = (byte)(i & 0xFF);        // timecode lo
      blockBody[3] = 0x00; // flags (no lacing, no keyframe bit in this simplified test)
      payload.CopyTo(blockBody, 4);
      WriteEbmlElement(clusterBody, 0xA3, clusterBody.ToArray().Length > 0 ? blockBody : blockBody);
      // Rewrite: just write the element
      clusterBody.SetLength(clusterBody.Length); // no-op, but clarify we don't truncate
    }

    // Actually re-build cluster properly
    var clusterMs = new MemoryStream();
    WriteEbmlElement(clusterMs, 0xE7, [0x00]); // Timecode = 0
    for (var i = 0; i < frameCount; ++i) {
      var payload = new byte[] { 0xAA, 0xBB, (byte)(i + 1) };
      var blockBody = new byte[1 + 2 + 1 + payload.Length];
      blockBody[0] = 0x81; // track 1
      blockBody[1] = (byte)((i >> 8) & 0xFF);
      blockBody[2] = (byte)(i & 0xFF);
      blockBody[3] = 0x00;
      payload.CopyTo(blockBody, 4);
      WriteEbmlElement(clusterMs, 0xA3, blockBody); // SimpleBlock
    }
    WriteEbmlElement(ms, 0x1F43B675, clusterMs.ToArray());

    return ms.ToArray();
  }

  /// <summary>Builds a minimal MKV with MJPEG video frames (each frame starts with FFD8).</summary>
  private static byte[] MakeMjpegMkv(int frameCount) {
    var ms = new MemoryStream();

    var ebmlBody = BuildEbmlHeaderBody();
    WriteEbmlElement(ms, 0x1A45DFA3, ebmlBody);

    WriteEbmlId(ms, 0x18538067);
    ms.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    var tracksBody = BuildTracksElement(1, "video", "V_MJPEG");
    WriteEbmlElement(ms, 0x1654AE6B, tracksBody);

    var clusterMs = new MemoryStream();
    WriteEbmlElement(clusterMs, 0xE7, [0x00]);
    for (var i = 0; i < frameCount; ++i) {
      var jpegPayload = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)(i + 1), 0xFF, 0xD9 };
      var blockBody = new byte[1 + 2 + 1 + jpegPayload.Length];
      blockBody[0] = 0x81;
      blockBody[3] = 0x00;
      jpegPayload.CopyTo(blockBody, 4);
      WriteEbmlElement(clusterMs, 0xA3, blockBody);
    }
    WriteEbmlElement(ms, 0x1F43B675, clusterMs.ToArray());

    return ms.ToArray();
  }

  private static byte[] BuildEbmlHeaderBody() {
    var inner = new MemoryStream();
    inner.Write(new byte[] { 0x42, 0x86, 0x81, 0x01 }); // EBMLVersion=1
    inner.WriteByte(0x42); inner.WriteByte(0x82); inner.WriteByte(0x88);
    inner.Write("matroska"u8);
    return inner.ToArray();
  }

  private static byte[] BuildTracksElement(int trackNumber, string trackType, string codecId) {
    // TrackEntry (0xAE) containing TrackNumber(0xD7), TrackType(0x83), CodecId(0x86)
    var entry = new MemoryStream();

    // TrackNumber (0xD7) = trackNumber
    WriteEbmlElement(entry, 0xD7, [(byte)trackNumber]);

    // TrackType (0x83): 1=video, 2=audio
    byte typeVal = trackType == "video" ? (byte)1 : trackType == "audio" ? (byte)2 : (byte)0;
    WriteEbmlElement(entry, 0x83, [typeVal]);

    // CodecId (0x86)
    var codecBytes = System.Text.Encoding.UTF8.GetBytes(codecId);
    WriteEbmlElement(entry, 0x86, codecBytes);

    // Wrap in TrackEntry (0xAE)
    var trackEntry = new MemoryStream();
    WriteEbmlElement(trackEntry, 0xAE, entry.ToArray());

    return trackEntry.ToArray();
  }

  private static void WriteEbmlId(MemoryStream ms, ulong id) {
    if (id <= 0xFF) {
      ms.WriteByte((byte)id);
    } else if (id <= 0xFFFF) {
      ms.WriteByte((byte)(id >> 8));
      ms.WriteByte((byte)(id & 0xFF));
    } else if (id <= 0xFFFFFF) {
      ms.WriteByte((byte)(id >> 16));
      ms.WriteByte((byte)((id >> 8) & 0xFF));
      ms.WriteByte((byte)(id & 0xFF));
    } else {
      ms.WriteByte((byte)(id >> 24));
      ms.WriteByte((byte)((id >> 16) & 0xFF));
      ms.WriteByte((byte)((id >> 8) & 0xFF));
      ms.WriteByte((byte)(id & 0xFF));
    }
  }

  private static void WriteEbmlSize(MemoryStream ms, int size) {
    if (size <= 127) {
      ms.WriteByte((byte)(0x80 | size));
    } else if (size <= 16383) {
      ms.WriteByte((byte)(0x40 | (size >> 8)));
      ms.WriteByte((byte)(size & 0xFF));
    } else {
      ms.WriteByte((byte)(0x20 | (size >> 16)));
      ms.WriteByte((byte)((size >> 8) & 0xFF));
      ms.WriteByte((byte)(size & 0xFF));
    }
  }

  private static void WriteEbmlElement(MemoryStream ms, ulong id, byte[] body) {
    WriteEbmlId(ms, id);
    WriteEbmlSize(ms, body.Length);
    ms.Write(body);
  }

  // ── MkvDemuxer per-frame tests ─────────────────────────────

  [Test]
  public void MkvDemuxer_PreservesPerFrameData() {
    var blob = MakeMkvWithVideoFrames(3);
    var result = new MkvDemuxer().Demux(blob);
    Assert.That(result.Tracks.Count, Is.EqualTo(1));
    var track = result.Tracks[0];
    Assert.That(track.TrackType, Is.EqualTo("video"));
    Assert.That(track.Frames.Count, Is.EqualTo(3));
    // Each frame's payload starts with 0xAA, 0xBB
    for (var i = 0; i < 3; ++i) {
      Assert.That(track.Frames[i].Data[0], Is.EqualTo(0xAA));
      Assert.That(track.Frames[i].Data[1], Is.EqualTo(0xBB));
      Assert.That(track.Frames[i].Data[2], Is.EqualTo(i + 1));
    }
  }

  // ── Descriptor frame entry tests ───────────────────────────

  [Test]
  public void Descriptor_ListsVideoFrameEntries() {
    var blob = MakeMkvWithVideoFrames(3);
    using var ms = new MemoryStream(blob);
    var entries = new MkvFormatDescriptor().List(ms, null);
    var frameEntries = entries.Where(e => e.Kind == "Frame").ToList();
    Assert.That(frameEntries.Count, Is.EqualTo(3));
    Assert.That(frameEntries[0].Name, Does.StartWith("frames/track_01/frame_000001"));
    Assert.That(frameEntries[2].Name, Does.Contain("frame_000003"));
  }

  [Test]
  public void Descriptor_AvcFrames_HaveH264Extension() {
    var blob = MakeMkvWithVideoFrames(1, "V_MPEG4/ISO/AVC");
    using var ms = new MemoryStream(blob);
    var entries = new MkvFormatDescriptor().List(ms, null);
    var frame = entries.First(e => e.Kind == "Frame");
    Assert.That(frame.Name, Does.EndWith(".h264"));
  }

  [Test]
  public void Descriptor_MjpegFrames_HaveJpgExtension() {
    var blob = MakeMjpegMkv(1);
    using var ms = new MemoryStream(blob);
    var entries = new MkvFormatDescriptor().List(ms, null);
    var frame = entries.First(e => e.Kind == "Frame");
    Assert.That(frame.Name, Does.EndWith(".jpg"));
  }

  [Test]
  public void Descriptor_ExtractFrame_ReturnsCorrectPayload() {
    var blob = MakeMkvWithVideoFrames(2);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    var desc = new MkvFormatDescriptor();
    var entries = desc.List(ms, null);
    var secondFrame = entries.Where(e => e.Kind == "Frame").Skip(1).First();

    ms.Position = 0;
    desc.ExtractEntry(ms, secondFrame.Name, output, null);
    var data = output.ToArray();
    Assert.That(data[0], Is.EqualTo(0xAA));
    Assert.That(data[2], Is.EqualTo(2)); // second frame payload byte
  }

  [Test]
  public void Descriptor_TrackEntryStillPresent() {
    var blob = MakeMkvWithVideoFrames(2);
    using var ms = new MemoryStream(blob);
    var entries = new MkvFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Track"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Frame"), Is.True);
  }
}
