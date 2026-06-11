using System.Text;
using FileFormat.Mp3;

namespace Compression.Tests.Mp3;

/// <summary>
/// Given an MP3 with ID3v1 trailer and MPEG-1 Layer III frames, When the
/// descriptor lists/extracts it, Then it surfaces id3v1.bin + per-frame blocks
/// alongside the verbatim FULL.mp3 — and never throws on malformed input.
/// </summary>
[TestFixture]
public class Mp3PseudoArchiveTests {

  // MPEG-1 Layer III, 128 kbps, 44100 Hz, no padding.
  // frameLen = 144 * bitrate / sampleRate = 144 * 128000 / 44100 = 417 bytes.
  private const int FrameLen = 417;

  private static byte[] MakeLayer3Frame(byte fill) {
    var frame = new byte[FrameLen];
    frame[0] = 0xFF;
    frame[1] = 0xFB;          // MPEG1, Layer III, no CRC
    frame[2] = 0x90;          // bitrate index 9 (128k), sample-rate index 0 (44100), no padding
    frame[3] = 0x00;          // mono, no emphasis
    for (var i = 4; i < FrameLen; ++i) frame[i] = fill;
    return frame;
  }

  private static byte[] MakeId3v1(string title) {
    var tag = new byte[128];
    tag[0] = (byte)'T'; tag[1] = (byte)'A'; tag[2] = (byte)'G';
    var t = Encoding.ASCII.GetBytes(title);
    Array.Copy(t, 0, tag, 3, Math.Min(t.Length, 30));
    return tag;
  }

  private static byte[] BuildMp3(int frameCount, bool withId3v1, out List<byte[]> frames) {
    frames = [];
    using var ms = new MemoryStream();
    for (var i = 0; i < frameCount; ++i) {
      var f = MakeLayer3Frame((byte)(0x10 + i));
      frames.Add(f);
      ms.Write(f);
    }
    if (withId3v1) ms.Write(MakeId3v1("PseudoArchive"));
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullAndPerFrameBlocks() {
    var mp3 = BuildMp3(3, withId3v1: true, out var frames);
    using var ms = new MemoryStream(mp3);
    var names = new Mp3FormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.mp3"));
    Assert.That(names, Does.Contain("id3v1.bin"));
    for (var i = 0; i < frames.Count; ++i)
      Assert.That(names, Does.Contain($"frames/frame_{i:D5}.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_PerFrameAndTrailer_FullByteIdentical() {
    var mp3 = BuildMp3(2, withId3v1: true, out var frames);
    var tmp = Path.Combine(Path.GetTempPath(), $"mp3-pa-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(mp3);
      new Mp3FormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.mp3")), Is.EqualTo(mp3));
      var trailer = File.ReadAllBytes(Path.Combine(tmp, "id3v1.bin"));
      Assert.That(trailer.Length, Is.EqualTo(128));
      Assert.That(trailer[0], Is.EqualTo((byte)'T'));
      for (var i = 0; i < frames.Count; ++i) {
        var path = Path.Combine(tmp, "frames", $"frame_{i:D5}.bin");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(frames[i]), $"frame {i} mismatch");
      }
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
    using var ms = new MemoryStream(bogus);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new Mp3FormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mp3"));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_ExtractedFullRereadsIdentically() {
    var mp3 = BuildMp3(2, withId3v1: false, out _);
    using var first = new MemoryStream(mp3);
    using var full = new MemoryStream();
    new Mp3FormatDescriptor().ExtractEntry(first, "FULL.mp3", full, null);
    Assert.That(full.ToArray(), Is.EqualTo(mp3));

    using var second = new MemoryStream(full.ToArray());
    var names = new Mp3FormatDescriptor().List(second, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("frames/frame_00000.bin"));
  }
}
