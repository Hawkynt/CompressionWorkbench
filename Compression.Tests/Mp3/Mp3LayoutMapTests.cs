#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Mp3;

namespace Compression.Tests.Mp3;

[TestFixture]
public class Mp3LayoutMapTests {

  /// <summary>
  /// Builds a minimal synthetic MP3 file:
  /// - ID3v2 header (10 bytes) + tag frames + padding (totalling <paramref name="id3v2PaddingSize"/> bytes of padding)
  /// - Audio frames (fake 1 KB region)
  /// - Optionally ID3v1 trailer (128 bytes)
  /// </summary>
  private static MemoryStream BuildTestMp3(int id3v2PaddingSize = 2048, bool includeId3v1 = true) {
    // Build an ID3v2 tag with a single TIT2 frame + padding
    var writer = new Id3v2Writer();
    writer.AddText("TIT2", "Test Title");
    var tagBytes = writer.Build();

    // The writer produces a tight tag with zero padding.
    // We need to expand it to include the requested padding.
    var originalTagSize = DecodeSyncSafe(tagBytes, 6);
    var newTagSize = originalTagSize + id3v2PaddingSize;

    // Rewrite the syncsafe size
    var expandedTag = new byte[10 + newTagSize];
    Array.Copy(tagBytes, 0, expandedTag, 0, tagBytes.Length);
    EncodeSyncSafe(expandedTag, 6, newTagSize);
    // Remaining bytes after tagBytes are already zero (padding)

    // Fake audio frames: 1024 bytes of 0xFF-prefixed data
    var audioFrames = new byte[1024];
    // First two bytes of an MPEG frame sync (Layer III, 128kbps, 44.1kHz, stereo)
    audioFrames[0] = 0xFF;
    audioFrames[1] = 0xFB;
    // Fill rest with non-zero to distinguish from padding
    for (var i = 2; i < audioFrames.Length; i++)
      audioFrames[i] = (byte)(i & 0xFF);

    var ms = new MemoryStream();
    ms.Write(expandedTag, 0, expandedTag.Length);
    ms.Write(audioFrames, 0, audioFrames.Length);

    if (includeId3v1) {
      // ID3v1: 128 bytes starting with "TAG"
      var id3v1 = new byte[128];
      id3v1[0] = (byte)'T';
      id3v1[1] = (byte)'A';
      id3v1[2] = (byte)'G';
      // Title: "Test Title" at offset 3, 30 bytes
      var title = System.Text.Encoding.Latin1.GetBytes("Test Title");
      Array.Copy(title, 0, id3v1, 3, title.Length);
      ms.Write(id3v1, 0, id3v1.Length);
    }

    ms.Position = 0;
    return ms;
  }

  private static int DecodeSyncSafe(byte[] data, int offset)
    => (data[offset] & 0x7F) << 21 |
       (data[offset + 1] & 0x7F) << 14 |
       (data[offset + 2] & 0x7F) << 7 |
       (data[offset + 3] & 0x7F);

  private static void EncodeSyncSafe(byte[] data, int offset, int value) {
    data[offset] = (byte)((value >> 21) & 0x7F);
    data[offset + 1] = (byte)((value >> 14) & 0x7F);
    data[offset + 2] = (byte)((value >> 7) & 0x7F);
    data[offset + 3] = (byte)(value & 0x7F);
  }

  // ──────────────────────────────────────────────────────────────────────
  // Layout Map Tests
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void EnumerateChunks_ReturnsId3v2Header() {
    using var ms = BuildTestMp3();
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var header = chunks.FirstOrDefault(c => c.FileName == "ID3v2 header");
    Assert.That(header, Is.Not.Null, "Expected ID3v2 header chunk");
    Assert.That(header!.Offset, Is.EqualTo(0));
    Assert.That(header.Length, Is.EqualTo(10));
    Assert.That(header.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_ReturnsId3v2TagFrames() {
    using var ms = BuildTestMp3();
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var frames = chunks.FirstOrDefault(c => c.FileName == "ID3v2 tag frames");
    Assert.That(frames, Is.Not.Null, "Expected ID3v2 tag frames chunk");
    Assert.That(frames!.Offset, Is.EqualTo(10));
    Assert.That(frames.Length, Is.GreaterThan(0));
    Assert.That(frames.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(frames.Classification, Is.EqualTo(DefragBlockClass.Hot));
  }

  [Test]
  public void EnumerateChunks_ReturnsId3v2Padding() {
    using var ms = BuildTestMp3(id3v2PaddingSize: 2048);
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var padding = chunks.FirstOrDefault(c => c.FileName == "ID3v2 padding");
    Assert.That(padding, Is.Not.Null, "Expected ID3v2 padding chunk");
    Assert.That(padding!.Length, Is.EqualTo(2048));
    Assert.That(padding.Kind, Is.EqualTo(DefragBlockKind.Free));
  }

  [Test]
  public void EnumerateChunks_ReturnsAudioFrames() {
    using var ms = BuildTestMp3();
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var audio = chunks.FirstOrDefault(c => c.FileName == "Audio frames");
    Assert.That(audio, Is.Not.Null, "Expected Audio frames chunk");
    Assert.That(audio!.Length, Is.EqualTo(1024));
    Assert.That(audio.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(audio.Classification, Is.EqualTo(DefragBlockClass.Normal));
  }

  [Test]
  public void EnumerateChunks_ReturnsId3v1Tag() {
    using var ms = BuildTestMp3(includeId3v1: true);
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var id3v1 = chunks.FirstOrDefault(c => c.FileName == "ID3v1 tag");
    Assert.That(id3v1, Is.Not.Null, "Expected ID3v1 tag chunk");
    Assert.That(id3v1!.Length, Is.EqualTo(128));
    Assert.That(id3v1.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(id3v1.Classification, Is.EqualTo(DefragBlockClass.Frozen));
  }

  [Test]
  public void EnumerateChunks_NoId3v1_WhenAbsent() {
    using var ms = BuildTestMp3(includeId3v1: false);
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var id3v1 = chunks.FirstOrDefault(c => c.FileName == "ID3v1 tag");
    Assert.That(id3v1, Is.Null, "Expected no ID3v1 tag chunk");
  }

  [Test]
  public void EnumerateChunks_CoversFullFile() {
    using var ms = BuildTestMp3();
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();

    var totalCovered = chunks.Sum(c => c.Length);
    Assert.That(totalCovered, Is.EqualTo(ms.Length),
      "All chunks should cover the full file length");
  }

  [Test]
  public void EnumerateChunks_ChunksAreContiguous() {
    using var ms = BuildTestMp3();
    var chunks = Mp3LayoutMap.Enumerate(ms).OrderBy(c => c.Offset).ToList();

    for (var i = 1; i < chunks.Count; i++) {
      var prev = chunks[i - 1];
      var curr = chunks[i];
      Assert.That(curr.Offset, Is.EqualTo(prev.Offset + prev.Length),
        $"Gap between chunk '{prev.FileName}' (end={prev.Offset + prev.Length}) and '{curr.FileName}' (start={curr.Offset})");
    }
  }

  [Test]
  public void Descriptor_ImplementsIFileInternalLayoutMap() {
    var d = new Mp3FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalLayoutMap>());
  }

  [Test]
  public void Descriptor_ImplementsIFileInternalChunkMover() {
    var d = new Mp3FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalChunkMover>());
  }

  // ──────────────────────────────────────────────────────────────────────
  // Optimizer Tests
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Optimize_CompactsPaddingTo256Bytes() {
    using var ms = BuildTestMp3(id3v2PaddingSize: 2048);
    var originalLength = ms.Length;

    Mp3Optimizer.Optimize(ms);

    // File should be shorter by (2048 - 256) = 1792 bytes
    Assert.That(ms.Length, Is.EqualTo(originalLength - (2048 - Mp3Optimizer.TargetPadding)));

    // Verify the new ID3v2 tag size is updated correctly
    ms.Position = 0;
    var header = new byte[10];
    ms.Read(header, 0, 10);
    Assert.That(header[0], Is.EqualTo((byte)'I'));
    Assert.That(header[1], Is.EqualTo((byte)'D'));
    Assert.That(header[2], Is.EqualTo((byte)'3'));

    var newTagSize = DecodeSyncSafe(header, 6);
    // Re-enumerate chunks and verify padding is now 256
    ms.Position = 0;
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();
    var padding = chunks.FirstOrDefault(c => c.FileName == "ID3v2 padding");
    Assert.That(padding, Is.Not.Null);
    Assert.That(padding!.Length, Is.EqualTo(Mp3Optimizer.TargetPadding));
  }

  [Test]
  public void Optimize_SyncSafeSizeUpdated() {
    using var ms = BuildTestMp3(id3v2PaddingSize: 4096);

    Mp3Optimizer.Optimize(ms);

    ms.Position = 6;
    var sizeBytes = new byte[4];
    ms.Read(sizeBytes, 0, 4);
    var newTagSize = DecodeSyncSafe(sizeBytes, 0);

    // Walk the chunks to verify frames + padding = declared size
    ms.Position = 0;
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();
    var framesChunk = chunks.First(c => c.FileName == "ID3v2 tag frames");
    var paddingChunk = chunks.First(c => c.FileName == "ID3v2 padding");
    Assert.That(newTagSize, Is.EqualTo(framesChunk.Length + paddingChunk.Length));
  }

  [Test]
  public void Optimize_AudioDataIntact() {
    using var ms = BuildTestMp3(id3v2PaddingSize: 2048, includeId3v1: true);

    // Capture original audio bytes
    var originalAudioStart = 10 + DecodeSyncSafe(ms.GetBuffer(), 6);
    var originalAudioBytes = new byte[1024];
    ms.Position = originalAudioStart;
    ms.Read(originalAudioBytes, 0, 1024);

    Mp3Optimizer.Optimize(ms);

    // Find the new audio start
    ms.Position = 0;
    var chunks = Mp3LayoutMap.Enumerate(ms).ToList();
    var audioChunk = chunks.First(c => c.FileName == "Audio frames");

    var newAudioBytes = new byte[1024];
    ms.Position = audioChunk.Offset;
    ms.Read(newAudioBytes, 0, 1024);

    Assert.That(newAudioBytes, Is.EqualTo(originalAudioBytes),
      "Audio data must be identical after optimization");
  }

  [Test]
  public void Optimize_Id3v1StillPresent() {
    using var ms = BuildTestMp3(id3v2PaddingSize: 2048, includeId3v1: true);

    Mp3Optimizer.Optimize(ms);

    // ID3v1 should still be the last 128 bytes
    ms.Position = ms.Length - 128;
    var tag = new byte[3];
    ms.Read(tag, 0, 3);
    Assert.That(tag[0], Is.EqualTo((byte)'T'));
    Assert.That(tag[1], Is.EqualTo((byte)'A'));
    Assert.That(tag[2], Is.EqualTo((byte)'G'));
  }

  [Test]
  public void Optimize_NoOp_WhenPaddingAlreadySmall() {
    using var ms = BuildTestMp3(id3v2PaddingSize: 100);
    var originalLength = ms.Length;

    Mp3Optimizer.Optimize(ms);

    Assert.That(ms.Length, Is.EqualTo(originalLength),
      "File should not change when padding is already <= 256 bytes");
  }

  [Test]
  public void Optimize_NoOp_WhenNoId3v2() {
    // Build a file with no ID3v2, just audio + ID3v1
    var ms = new MemoryStream();
    var audio = new byte[1024];
    audio[0] = 0xFF;
    audio[1] = 0xFB;
    ms.Write(audio, 0, audio.Length);

    var id3v1 = new byte[128];
    id3v1[0] = (byte)'T';
    id3v1[1] = (byte)'A';
    id3v1[2] = (byte)'G';
    ms.Write(id3v1, 0, 128);

    var originalLength = ms.Length;
    Mp3Optimizer.Optimize(ms);

    Assert.That(ms.Length, Is.EqualTo(originalLength),
      "File should not change when there is no ID3v2 tag");
  }

  [Test]
  public void Optimize_ExactlyTargetPadding_IsNoOp() {
    using var ms = BuildTestMp3(id3v2PaddingSize: Mp3Optimizer.TargetPadding);
    var originalLength = ms.Length;

    Mp3Optimizer.Optimize(ms);

    Assert.That(ms.Length, Is.EqualTo(originalLength),
      "File should not change when padding is exactly 256 bytes");
  }
}
