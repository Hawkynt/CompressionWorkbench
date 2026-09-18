using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.FilesystemDrivers;

/// <summary>
/// The bounded positional handle the mounted NTFS session opens over a proven
/// $DATA layout. The profiles a volume can present it with — resident bytes,
/// cluster runs, holes, an uninitialized tail — are exercised here against
/// synthetic layouts, because an image writer cannot produce all of them.
/// </summary>
[TestFixture]
public sealed class NtfsDirectReadOnlyFileHandleTests {
  private const int ClusterSize = 512;
  private const int ImageClusters = 6;

  private static byte[] ImageBytes() {
    var bytes = new byte[ImageClusters * ClusterSize];
    for (var i = 0; i < bytes.Length; ++i)
      bytes[i] = (byte)(i * 31 + 7);
    return bytes;
  }

  private static byte[] Cluster(byte[] image, int lcn) => image.AsSpan(lcn * ClusterSize, ClusterSize).ToArray();

  private static NtfsDirectReadOnlyFileHandle Open(Stream image, NtfsMountedDataLayout layout)
    => new(image, new object(), new FilesystemNodeId(42, 1), layout, ClusterSize);

  private static byte[] ReadAll(IFilesystemFileHandle handle) {
    var buffer = new byte[handle.Length];
    Assert.That(handle.Read(0, buffer), Is.EqualTo(buffer.Length));
    return buffer;
  }

  // ── what the handle assembles ───────────────────────────────────────────

  [Test]
  public void ResidentDataIsServedFromTheRecord() {
    var resident = "the quick brown fox"u8.ToArray();
    using var image = new MemoryStream(ImageBytes(), writable: false);
    using var handle = Open(image, new NtfsMountedDataLayout(resident.Length, resident.Length, resident, []));

    Assert.Multiple(() => {
      Assert.That(handle.Length, Is.EqualTo(resident.Length));
      Assert.That(ReadAll(handle), Is.EqualTo(resident));
      var middle = new byte[5];
      Assert.That(handle.Read(4, middle), Is.EqualTo(5));
      Assert.That(middle, Is.EqualTo(resident.AsSpan(4, 5).ToArray()));
    });
  }

  [Test]
  public void OutOfOrderRunsAreAssembledInLogicalOrder() {
    // Runs are VCN-ordered but their clusters need not be: a fragmented file
    // maps later logical bytes to earlier clusters.
    var bytes = ImageBytes();
    using var image = new MemoryStream(bytes, writable: false);
    var layout = new NtfsMountedDataLayout(2 * ClusterSize, 2 * ClusterSize, null, [
      new NtfsMountedDataRun(0, 4, 1, false),
      new NtfsMountedDataRun(1, 1, 1, false),
    ]);
    using var handle = Open(image, layout);

    var expected = Cluster(bytes, 4).Concat(Cluster(bytes, 1)).ToArray();
    Assert.That(ReadAll(handle), Is.EqualTo(expected));

    // A read that straddles the run boundary must stitch the two halves.
    var across = new byte[64];
    Assert.That(handle.Read(ClusterSize - 32, across), Is.EqualTo(64));
    Assert.That(across, Is.EqualTo(expected.AsSpan(ClusterSize - 32, 64).ToArray()));
  }

  [Test]
  public void SparseRunsReadAsZeroesWithoutTouchingTheImage() {
    var bytes = ImageBytes();
    using var image = new MemoryStream(bytes, writable: false);
    var layout = new NtfsMountedDataLayout(3 * ClusterSize, 3 * ClusterSize, null, [
      new NtfsMountedDataRun(0, 2, 1, false),
      new NtfsMountedDataRun(1, 0, 1, true),
      new NtfsMountedDataRun(2, 3, 1, false),
    ]);
    using var handle = Open(image, layout);

    var expected = Cluster(bytes, 2).Concat(new byte[ClusterSize]).Concat(Cluster(bytes, 3)).ToArray();
    Assert.That(ReadAll(handle), Is.EqualTo(expected));
  }

  [Test]
  public void BytesPastTheInitializedLengthReadAsZeroes() {
    // The clusters behind an uninitialized tail still hold whatever the volume
    // last wrote there; NTFS says a reader must not see it.
    var bytes = ImageBytes();
    using var image = new MemoryStream(bytes, writable: false);
    var layout = new NtfsMountedDataLayout(2 * ClusterSize, 300, null, [
      new NtfsMountedDataRun(0, 1, 2, false),
    ]);
    using var handle = Open(image, layout);

    var expected = new byte[2 * ClusterSize];
    bytes.AsSpan(ClusterSize, 300).CopyTo(expected);
    Assert.That(ReadAll(handle), Is.EqualTo(expected));

    // A read that starts inside the uninitialized region reads nothing but zeroes.
    var tail = new byte[64];
    Assert.That(handle.Read(400, tail), Is.EqualTo(64));
    Assert.That(tail, Is.EqualTo(new byte[64]));
  }

  [Test]
  public void AnEmptyStreamHasNothingToRead() {
    using var image = new MemoryStream(ImageBytes(), writable: false);
    using var handle = Open(image, new NtfsMountedDataLayout(0, 0, [], []));

    Assert.Multiple(() => {
      Assert.That(handle.Length, Is.Zero);
      Assert.That(handle.Read(0, new byte[16]), Is.Zero);
    });
  }

  // ── offset and length boundaries ────────────────────────────────────────

  [TestCase(0, 16, 16)]
  [TestCase(ClusterSize - 1, 16, 1)]
  [TestCase(ClusterSize, 16, 0)]
  [TestCase(ClusterSize + 1, 16, 0)]
  [TestCase(0, 0, 0)]
  public void ReadsAreClampedToTheLogicalLength(int offset, int count, int expected) {
    using var image = new MemoryStream(ImageBytes(), writable: false);
    var layout = new NtfsMountedDataLayout(ClusterSize, ClusterSize, null, [new NtfsMountedDataRun(0, 1, 1, false)]);
    using var handle = Open(image, layout);

    Assert.That(handle.Read(offset, new byte[count]), Is.EqualTo(expected));
  }

  [Test]
  public void ANegativeOffsetIsRejected() {
    using var image = new MemoryStream(ImageBytes(), writable: false);
    var layout = new NtfsMountedDataLayout(ClusterSize, ClusterSize, null, [new NtfsMountedDataRun(0, 1, 1, false)]);
    using var handle = Open(image, layout);

    Assert.Throws<ArgumentOutOfRangeException>(() => handle.Read(-1, new byte[16]));
  }

  // ── refusals ────────────────────────────────────────────────────────────

  [Test]
  public void TheHandleIsReadOnly() {
    using var image = new MemoryStream(ImageBytes(), writable: false);
    using var handle = Open(image, new NtfsMountedDataLayout(0, 0, [], []));

    Assert.Multiple(() => {
      Assert.Throws<NotSupportedException>(() => handle.Write(0, "x"u8));
      Assert.Throws<NotSupportedException>(() => handle.SetLength(1));
    });
  }

  [Test]
  public void ADisposedHandleServesNothing() {
    using var image = new MemoryStream(ImageBytes(), writable: false);
    var handle = Open(image, new NtfsMountedDataLayout(0, 0, [], []));
    handle.Dispose();

    Assert.Multiple(() => {
      Assert.Throws<ObjectDisposedException>(() => handle.Read(0, new byte[1]));
      Assert.Throws<ObjectDisposedException>(() => _ = handle.Length);
      Assert.Throws<ObjectDisposedException>(handle.Flush);
    });
  }

  [Test]
  public void ARunOutsideTheImageIsRefusedRatherThanReadShort() {
    using var image = new MemoryStream(ImageBytes(), writable: false);
    var layout = new NtfsMountedDataLayout(ClusterSize, ClusterSize, null, [
      new NtfsMountedDataRun(0, ImageClusters + 4, 1, false),
    ]);
    using var handle = Open(image, layout);

    Assert.Throws<InvalidDataException>(() => handle.Read(0, new byte[ClusterSize]));
  }

  [Test]
  public void TheHandleRefusesAnImageItCannotSeek() {
    var layout = new NtfsMountedDataLayout(0, 0, [], []);
    using var forwardOnly = new ForwardOnlyStream();

    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => Open(null!, layout));
      Assert.Throws<ArgumentException>(() => Open(forwardOnly, layout));
      Assert.Throws<ArgumentNullException>(() =>
        _ = new NtfsDirectReadOnlyFileHandle(new MemoryStream(), new object(), default, null!, ClusterSize));
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        _ = new NtfsDirectReadOnlyFileHandle(new MemoryStream(), new object(), default, layout, 0));
    });
  }

  private sealed class ForwardOnlyStream : MemoryStream {
    public override bool CanSeek => false;
  }
}
