using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// The streaming-create and rebuild-mutation paths that promote Reiser4 to R/W, judged by
/// reiser4progs rather than by our own reader: <c>fsck.reiser4</c> must call every volume
/// consistent and <c>debugfs.reiser4</c> must see the files by name. A self round-trip alone
/// would pass even if reader and writer agreed on a wrong layout.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class Reiser4MutationExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4mut_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireReiser4Progs() {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available on this machine.");
    if (!FsInteropToolbox.WslHasTool("fsck.reiser4") || !FsInteropToolbox.WslHasTool("debugfs.reiser4"))
      Assert.Ignore("reiser4progs is not installed (`sudo apt install -y reiser4progs`).");
  }

  private static byte[] Payload(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private void AssertReiser4ProgsAccept(byte[] image, params string[] expectedNames) {
    var path = Path.Combine(this._tmpDir, "volume.img");
    File.WriteAllBytes(path, image);
    var wslPath = FsInteropToolbox.WinToWsl(path);

    var fsck = FsInteropToolbox.RunWsl($"fsck.reiser4 -y {wslPath}");
    var fsckText = fsck.StdOut + "\n" + fsck.StdErr;
    Assert.That(fsck.ExitCode, Is.EqualTo(0), $"fsck.reiser4 rejected the volume:\n{fsckText}");
    Assert.That(fsckText, Does.Contain("consistent"), $"fsck.reiser4 exited 0 without calling the volume consistent:\n{fsckText}");

    var listing = FsInteropToolbox.RunWsl($"debugfs.reiser4 -k / {wslPath}");
    var listingText = listing.StdOut + "\n" + listing.StdErr;
    Assert.That(listing.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 failed:\n{listingText}");
    foreach (var name in expectedNames)
      Assert.That(listingText, Does.Contain(name), $"debugfs.reiser4 does not see '{name}':\n{listingText}");
  }

  [Test, Category("HappyPath")]
  public void GivenStreamedInputsAcrossABitmapBoundary_WhenCreatingFromStreams_ThenReiser4ProgsAcceptTheVolume() {
    RequireReiser4Progs();
    var large = Payload(2 * 1024 * 1024 + 37, 5);
    var small = Payload(1, 6);
    using var image = new MemoryStream();
    new Reiser4FormatDescriptor().CreateFromStreams(image, [
      new StreamingArchiveInput("streamed.bin", large.LongLength, false, () => new MemoryStream(large, writable: false)),
      new StreamingArchiveInput("tiny.bin", small.LongLength, false, () => new MemoryStream(small, writable: false)),
    ], new FormatCreateOptions());

    this.AssertReiser4ProgsAccept(image.ToArray(), "streamed.bin", "tiny.bin");
  }

  [TestCase(DefragMode.ConsolidateAtEnd, TestName = "GivenFilesAtTheStart_WhenConsolidatingAtTheEnd_ThenTheRepointedTwigExtentsPassFsck")]
  [TestCase(DefragMode.ConsolidateAtStart, TestName = "GivenFilesAtTheStart_WhenConsolidatingAtTheStart_ThenTheVolumeStillPassesFsck")]
  public void Defragment_MovesBodiesAndKeepsTheTreeConsistent(DefragMode mode) {
    RequireReiser4Progs();
    var files = Enumerable.Range(0, 4).ToDictionary(i => $"F{i}.BIN", i => Payload(9_000 + i * 7_000, 20 + i));
    var writer = new Reiser4Writer();
    foreach (var (name, data) in files) writer.AddFile(name, data);
    using var image = new MemoryStream();
    writer.Write(image);
    var before = image.ToArray();

    image.Position = 0;
    new Reiser4FormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    if (mode == DefragMode.ConsolidateAtEnd)
      Assert.That(image.ToArray(), Is.Not.EqualTo(before), "consolidating at the end has to move the bodies");

    this.AssertReiser4ProgsAccept(image.ToArray(), [.. files.Keys]);
    image.Position = 0;
    using var reader = new Reiser4Reader(image, leaveOpen: true);
    foreach (var (name, data) in files)
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == name)), Is.EqualTo(data), name);
  }

  [Test, Category("HappyPath")]
  public void GivenAnExistingVolume_WhenAddingReplacingAndRemovingByRebuild_ThenReiser4ProgsAcceptEveryResult() {
    RequireReiser4Progs();
    var descriptor = new Reiser4FormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;
    using var image = new MemoryStream();
    descriptor.Create(image, [ArchiveInputInfo.InMemory("original.bin", Payload(5_123, 1))], new FormatCreateOptions());

    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory("added.bin", Payload(8_765, 2))]);
    this.AssertReiser4ProgsAccept(image.ToArray(), "original.bin", "added.bin");

    var replacement = Payload(40_000, 3);
    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory("original.bin", replacement)]);
    this.AssertReiser4ProgsAccept(image.ToArray(), "original.bin", "added.bin");

    image.Position = 0;
    modifier.Remove(image, ["added.bin"]);
    this.AssertReiser4ProgsAccept(image.ToArray(), "original.bin");

    image.Position = 0;
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Select(static e => e.Name), Is.EquivalentTo(new[] { "original.bin" }));
      Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(replacement));
    });
  }
}
