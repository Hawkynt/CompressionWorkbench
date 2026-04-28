#pragma warning disable CS1591
using Compression.Analysis;
using FileSystem.Fat;

namespace Compression.Tests.Analysis;

[TestFixture]
public class RecursiveFilesystemCarverTests {

  private static byte[] BuildFat(int extraBytes = 0) {
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", "Hello"u8.ToArray());
    fat.AddFile("DATA.BIN", "World"u8.ToArray());
    var img = fat.Build();
    if (extraBytes <= 0) return img;
    var padded = new byte[img.Length + extraBytes];
    img.CopyTo(padded, 0);
    return padded;
  }

  [Test]
  public void PlainFat_FoundAtDepth0() {
    var img = BuildFat();
    using var ms = new MemoryStream(img);
    var carver = new RecursiveFilesystemCarver { MaxDepth = 5 };
    var hits = carver.CarveStream(ms);
    Assert.That(hits, Is.Not.Empty, "Should find at least the FAT at depth 0");
    Assert.That(hits[0].Depth, Is.EqualTo(0));
    Assert.That(hits[0].FormatId, Does.Contain("Fat").IgnoreCase);
    Assert.That(hits[0].EnvelopeStack[^1], Does.Contain("Fat").IgnoreCase);
  }

  [Test]
  public void EmptyStream_NoHits() {
    using var ms = new MemoryStream();
    var carver = new RecursiveFilesystemCarver();
    Assert.That(carver.CarveStream(ms), Is.Empty);
  }

  [Test]
  public void RandomBytes_NoHits_OrLowConfidence() {
    var random = new byte[64 * 1024];
    new Random(42).NextBytes(random);
    using var ms = new MemoryStream(random);
    var carver = new RecursiveFilesystemCarver { MinConfidence = 0.7 };
    var hits = carver.CarveStream(ms);
    // High-confidence threshold should reject any random-byte false positives.
    foreach (var h in hits)
      Assert.That(h.Confidence, Is.GreaterThanOrEqualTo(0.7));
  }

  [Test]
  public void MaxDepthZero_ReturnsEmpty() {
    var img = BuildFat();
    using var ms = new MemoryStream(img);
    var carver = new RecursiveFilesystemCarver { MaxDepth = 0 };
    Assert.That(carver.CarveStream(ms), Is.Empty);
  }

  [Test]
  public void CarveStream_RejectsNonSeekableStream() {
    using var pipe = new PipeStreamShim();
    var carver = new RecursiveFilesystemCarver();
    Assert.Throws<ArgumentException>(() => carver.CarveStream(pipe));
  }

  [Test]
  public void EnvelopeStack_IsOutermostFirst() {
    var img = BuildFat();
    using var ms = new MemoryStream(img);
    var hits = new RecursiveFilesystemCarver().CarveStream(ms);
    foreach (var h in hits) {
      Assert.That(h.EnvelopeStack.Count, Is.GreaterThan(0));
      Assert.That(h.EnvelopeStack[^1], Is.EqualTo(h.FormatId), "Last EnvelopeStack entry must equal FormatId");
    }
  }

  private sealed class PipeStreamShim : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
