using Compression.Registry;
using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// External forward gate for populated and subsequently edited GFS2 volumes.
/// Internal round-trips are necessary but insufficient here: the real
/// <c>fsck.gfs2</c> must accept the allocation bitmaps, dinodes, indirect tree,
/// root directory and statfs accounting after every public R/W operation.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class Gfs2MutationExternalTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_gfs2rw_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireGfs2Utils() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Install a WSL distro, then inside it run `sudo apt install -y gfs2-utils`.");
    if (!FsInteropToolbox.WslHasTool("fsck.gfs2"))
      Assert.Ignore("gfs2-utils not found in WSL. Install with `sudo apt install -y gfs2-utils`.");
  }

  private static void AssertFsckClean(string imagePath) {
    var fsck = FsInteropToolbox.RunWsl($"fsck.gfs2 -n {FsInteropToolbox.WinToWsl(imagePath)}");
    Assert.That(fsck.ExitCode, Is.EqualTo(0),
      $"fsck.gfs2 -n rejected the volume:\nstdout:\n{fsck.StdOut}\nstderr:\n{fsck.StdErr}");
    Assert.That(fsck.StdOut, Does.Contain("complete").IgnoreCase,
      $"fsck.gfs2 did not report completion:\n{fsck.StdOut}");
    foreach (var bad in new[] { "failed", "damage", "cannot fix", "does not match", "corrupt" })
      Assert.That(fsck.StdOut, Does.Not.Contain(bad).IgnoreCase,
        $"fsck.gfs2 reported a problem ('{bad}'):\n{fsck.StdOut}");
  }

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(seed * 41 + i * 13 + i / 4096);
    return data;
  }

  [Test, Category("HappyPath")]
  public void PopulatedCreate_AddReplaceRemove_RemainFsckClean() {
    RequireGfs2Utils();
    var path = Path.Combine(this._tmpDir, "mutated.gfs2");
    var descriptor = new Gfs2FormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;
    var seed = Payload(1, 9_000);
    var extra = Payload(2, 20_000);
    var replacement = Payload(3, 33_000);

    using (var image = File.Create(path))
      descriptor.Create(image,
        [ArchiveInputInfo.InMemory("SEED.BIN", seed)],
        new FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["size"] = "64M",
            ["LockTable"] = "cluster:fsck-rw",
          },
        });
    AssertFsckClean(path);

    using (var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      modifier.Add(image, [ArchiveInputInfo.InMemory("EXTRA.BIN", extra)]);
    AssertFsckClean(path);

    using (var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      modifier.Add(image, [ArchiveInputInfo.InMemory("EXTRA.BIN", replacement)]);
    AssertFsckClean(path);

    using (var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      modifier.Remove(image, ["SEED.BIN"]);
    AssertFsckClean(path);

    using var read = File.OpenRead(path);
    using var reader = new Gfs2Reader(read);
    Assert.Multiple(() => {
      Assert.That(reader.LockTable, Is.EqualTo("cluster:fsck-rw"));
      Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "EXTRA.BIN" }));
      Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(replacement));
    });
  }
}
