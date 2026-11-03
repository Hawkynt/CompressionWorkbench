using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Registry;

/// <summary>
/// <see cref="ArchiveInputInfo.ReadObserver"/> counts the bytes of every on-disk input read
/// while an archive is created, for a host's progress bar.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 0e5d174.</remarks>
[TestFixture]
[NonParallelizable] // the observer is process-wide
public sealed class ArchiveInputReadObserverTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_progress_").FullName;

  [TearDown]
  public void TearDown() {
    ArchiveInputInfo.ReadObserver = null;
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  [Test, Category("HappyPath")]
  public void GivenAnObserver_WhenATarIsCreatedFromFiles_ThenItSeesEveryInputByte() {
    var a = Path.Combine(this._dir, "a.bin");
    var b = Path.Combine(this._dir, "b.bin");
    File.WriteAllBytes(a, new byte[12345]);
    File.WriteAllBytes(b, new byte[678]);
    long seen = 0;
    ArchiveInputInfo.ReadObserver = n => Interlocked.Add(ref seen, n);

    ArchiveOperations.Create(Path.Combine(this._dir, "t.tar"), [new ArchiveInput(a, "a.bin"), new ArchiveInput(b, "b.bin")], new CompressionOptions());

    Assert.That(Interlocked.Read(ref seen), Is.EqualTo(12345 + 678));
  }

  [Test, Category("EdgeCase")]
  public void GivenAnObserver_WhenAnInMemoryInputIsRead_ThenNothingIsReported() {
    long seen = 0;
    ArchiveInputInfo.ReadObserver = n => Interlocked.Add(ref seen, n);

    _ = ArchiveInputInfo.InMemory("m.bin", new byte[99]).ReadContent();

    Assert.That(Interlocked.Read(ref seen), Is.Zero);
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEmptyFile_WhenRead_ThenZeroBytesAreReported() {
    var empty = Path.Combine(this._dir, "empty.bin");
    File.WriteAllBytes(empty, []);
    var calls = new List<long>();
    ArchiveInputInfo.ReadObserver = calls.Add;

    _ = new ArchiveInputInfo(empty, "empty.bin", false).ReadContent();

    Assert.That(calls, Is.EqualTo(new long[] { 0 }));
  }

  [Test, Category("HappyPath")]
  public void GivenNoObserver_WhenAFileIsRead_ThenItsBytesComeBack() {
    var f = Path.Combine(this._dir, "f.bin");
    File.WriteAllBytes(f, [1, 2, 3]);

    Assert.That(new ArchiveInputInfo(f, "f.bin", false).ReadContent(), Is.EqualTo(new byte[] { 1, 2, 3 }));
  }
}
