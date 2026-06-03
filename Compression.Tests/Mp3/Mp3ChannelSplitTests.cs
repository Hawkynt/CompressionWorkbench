#pragma warning disable CS1591
using Compression.Tests.Codecs.Mp3;
using FileFormat.Mp3;

namespace Compression.Tests.Mp3;

/// <summary>
/// Pins the MP3 descriptor's decode-and-split wiring: the decoder is attempted on
/// the audio frames and, when it can't produce PCM (Layer I/II, header-only, or a
/// tag-only file), the archive still surfaces <c>FULL.mp3</c> + metadata without
/// throwing. (A positive per-channel split needs a real Layer III vector, exercised
/// at the <c>Codec.Mp3</c> level.)
/// </summary>
[TestFixture]
public class Mp3ChannelSplitTests {

  [Test]
  public void Mp3Descriptor_TagOnlyFile_FallsBackToFullWithoutThrowing() {
    // ID3v2 header + a small text frame, no MPEG audio frames.
    var blob = BuildId3v2OnlyMp3();
    using var ms = new MemoryStream(blob);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new Mp3FormatDescriptor().List(ms, null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.mp3"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void Mp3Descriptor_GarbageFrames_DoNotThrow() {
    var blob = new byte[256];
    for (var i = 0; i < blob.Length; ++i) blob[i] = (byte)(i * 37 + 11);
    using var ms = new MemoryStream(blob);
    Assert.That(() => new Mp3FormatDescriptor().List(ms, null), Throws.Nothing);
  }

  [Test]
  public void Mp3Descriptor_LayerIIMonoSilence_SurfacesMonoChannel() {
    var frame = Mp3SyntheticFrames.BuildLayerIIMonoSilenceFrame();
    var blob = new byte[frame.Length * 2];
    frame.CopyTo(blob, 0);
    frame.CopyTo(blob, frame.Length);

    using var ms = new MemoryStream(blob);
    var entries = new Mp3FormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.mp3"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name is "LEFT.wav" or "RIGHT.wav"), Is.False);
  }

  [Test]
  public void Mp3Descriptor_LayerIIStereoSilence_SurfacesLeftRightChannels() {
    var frame = Mp3SyntheticFrames.BuildLayerIIStereoSilenceFrame();
    var blob = new byte[frame.Length * 2];
    frame.CopyTo(blob, 0);
    frame.CopyTo(blob, frame.Length);

    using var ms = new MemoryStream(blob);
    var entries = new Mp3FormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.mp3"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False);
  }

  private static byte[] BuildId3v2OnlyMp3() {
    // "ID3" + version 4.0 + flags 0 + a 4-byte synchsafe size covering one TIT2 frame.
    var title = "Title"u8.ToArray();
    var frameSize = 1 + title.Length;                // encoding byte + text
    var frame = new byte[10 + frameSize];
    "TIT2"u8.CopyTo(frame);
    frame[4] = 0; frame[5] = 0; frame[6] = 0; frame[7] = (byte)frameSize; // 32-bit size (small)
    frame[10] = 0x03;                                 // UTF-8
    title.CopyTo(frame.AsSpan(11));

    var tagBody = frame;
    var header = new byte[10];
    "ID3"u8.CopyTo(header);
    header[3] = 4; header[4] = 0; header[5] = 0;      // v2.4.0, no flags
    var sz = tagBody.Length;                           // synchsafe (small enough to fit 7 bits)
    header[6] = (byte)((sz >> 21) & 0x7F);
    header[7] = (byte)((sz >> 14) & 0x7F);
    header[8] = (byte)((sz >> 7) & 0x7F);
    header[9] = (byte)(sz & 0x7F);

    var blob = new byte[header.Length + tagBody.Length];
    header.CopyTo(blob, 0);
    tagBody.CopyTo(blob, header.Length);
    return blob;
  }
}
