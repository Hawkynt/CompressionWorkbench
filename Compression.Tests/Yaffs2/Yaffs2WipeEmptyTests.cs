using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Yaffs2;

/// <summary>
/// Behaviour: YAFFS2 is a log-structured flash filesystem. File data lives in
/// fixed 2 KiB chunks each carrying a packed-tags2 spare (OOB) region; there is
/// no in-place cluster-tip slack to scrub because every chunk's used byte count
/// is recorded in its spare and the unused chunk tail is part of the logged
/// chunk. The meaningful unused space is therefore free (unallocated) chunks.
/// This verifies a dirtied free chunk is zeroed while live files round-trip.
/// </summary>
[TestFixture]
public class Yaffs2WipeEmptyTests {
  private const int ChunkSize = 2048;
  private const int SpareSize = 64;
  private const int Stride = ChunkSize + SpareSize;

  [Test, Category("HappyPath"), Category("WipeEmpty")]
  public void WipeUnusedSpace_ZeroesFreeChunk_AndPreservesFile() {
    // Given an image with one small file (content < one chunk) plus a trailing
    // free (unallocated) chunk that has been dirtied.
    var content = new byte[200];
    Array.Fill(content, (byte)0xAA);

    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("tip.bin", content);
    var image = w.Build();

    // Append a dirtied trailing free region. It is shorter than a full
    // chunk stride, so the scanner classifies the leftover bytes as Free
    // (not a header or data chunk) and the wiper must scrub them.
    const int freeLen = 500;
    var withFree = new byte[image.Length + freeLen];
    image.CopyTo(withFree, 0);
    for (var i = image.Length; i < withFree.Length; i++) withFree[i] = 0xBB;
    var freeChunkOffset = image.Length;

    using var ms = new MemoryStream();
    ms.Write(withFree);
    ms.Position = 0;

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();

    // When unused space is wiped.
    var wiped = ((IWipeEmpty)d).WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then something was scrubbed.
    Assert.That(wiped, Is.GreaterThan(0));

    // And the free region is now all zero.
    ms.Position = 0;
    var buf = ms.ToArray();
    for (var i = freeChunkOffset; i < freeChunkOffset + freeLen; i++)
      Assert.That(buf[i], Is.EqualTo(0), $"free byte at {i} must be zeroed");

    // And the file round-trips intact via the scanner.
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(buf);
    Assert.That(scan.ParseOk, Is.True);
    var fileObj = scan.Objects.First(o =>
      o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File && o.Name == "tip.bin");
    var chunks = scan.DataChunks[fileObj.ObjectId];
    var data = chunks.SelectMany(c => buf.Skip((int)c.Offset).Take(c.Length)).ToArray();
    Assert.That(data[..content.Length], Is.EqualTo(content), "file content must survive the wipe");
  }
}
