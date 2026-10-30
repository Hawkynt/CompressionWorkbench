#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// FreeBSD's own <c>fsck_ffs</c> (the <c>fsck.ufs</c> build of Debian's ufsutils 8.2)
/// judges volumes the writer and the in-place modifier produce. Skips when the tool
/// is not on the WSL PATH — ufsutils is no longer packaged by current Ubuntu; the
/// archived <c>ufsutils_8.2-3_amd64.deb</c> from archive.debian.org runs unchanged.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Wsl")]
public sealed class UfsFsckTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    _tmp = Path.Combine(Path.GetTempPath(), $"cwb_ufsfsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
  }

  private static void RequireFsck() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("fsck.ufs"))
      Assert.Ignore("fsck.ufs (FreeBSD fsck_ffs from ufsutils) is not on the WSL PATH.");
  }

  private (int Exit, string Output) Fsck(byte[] image) {
    var path = Path.Combine(_tmp, $"v{Guid.NewGuid():N}.ufs");
    File.WriteAllBytes(path, image);
    var r = FsInteropToolbox.RunWsl($"fsck.ufs -n {FsInteropToolbox.WinToWsl(path)} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private static void AssertClean((int Exit, string Output) fsck) {
    Assert.That(fsck.Exit, Is.Zero, fsck.Output);
    Assert.That(fsck.Output, Does.Not.Contain("UNREF").And.Not.Contain("BAD").And.Not.Contain("INCORRECT")
      .And.Not.Contain("WRONG").And.Not.Contain("SALVAGE").And.Not.Contain("FIX?"), fsck.Output);
  }

  private static byte[] Create(params (string Name, byte[] Data)[] files) {
    var d = new UfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToList(), new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test]
  public void GivenAFreshVolumeWithNestedDirectories_WhenChecked_ThenFsckFfsIsClean() {
    RequireFsck();
    AssertClean(Fsck(Create(("top.txt", "t"u8.ToArray()), ("a/b/c.txt", "c"u8.ToArray()))));
  }

  [Test]
  public void GivenAFileAddedIntoNewSubdirectories_WhenChecked_ThenFsckFfsIsCleanAndTheTreeIsReal() {
    RequireFsck();
    var image = Create(("seed.txt", "seed"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);
    var d = new UfsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("docs/notes/today.txt", Encoding.ASCII.GetBytes("written later\n"))]);
    d.Add(ms, [ArchiveInputInfo.InMemory("docs/second.txt", "2"u8.ToArray())]);

    AssertClean(Fsck(ms.ToArray()));
    ms.Position = 0;
    using var r = new UfsReader(ms);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.SupersetOf(new[] { "docs", "docs/notes", "docs/notes/today.txt", "docs/second.txt" }));
    Assert.That(names.Where(n => !n.Contains('/')), Is.EquivalentTo(new[] { "seed.txt", "docs" }),
      "no root entry carries a slash");
    Assert.That(r.Entries.Single(e => e.Name == "docs").IsDirectory, Is.True);
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "docs/notes/today.txt"))), Is.EqualTo("written later\n"));
  }

  [Test]
  public void GivenASubdirectoryGrownPastSeveralChunks_WhenChecked_ThenFsckFfsIsCleanAndEveryFileReadsBack() {
    RequireFsck();
    using var ms = new MemoryStream();
    ms.Write(Create(("seed.txt", "seed"u8.ToArray())));
    var d = new UfsFormatDescriptor();
    // 3000+ bytes of directory entries: the directory grows from one DIRBLKSIZ chunk
    // through its first fragment into a moved, longer fragment run.
    for (var i = 0; i < 120; ++i)
      d.Add(ms, [ArchiveInputInfo.InMemory($"many/file-with-a-longish-name-{i:D3}.txt", Encoding.ASCII.GetBytes($"payload {i}"))]);
    d.Add(ms, [ArchiveInputInfo.InMemory("other.bin", new byte[20_000])]);

    AssertClean(Fsck(ms.ToArray()));
    ms.Position = 0;
    using var r = new UfsReader(ms);
    var files = r.Entries.Where(e => e.Name.StartsWith("many/", StringComparison.Ordinal)).ToList();
    Assert.That(files, Has.Count.EqualTo(120));
    foreach (var e in files)
      Assert.That(Encoding.ASCII.GetString(r.Extract(e)), Is.EqualTo("payload " + int.Parse(e.Name[^7..^4])));
  }

  [Test]
  public void GivenANestedFileRemoved_WhenChecked_ThenFsckFfsIsClean() {
    RequireFsck();
    using var ms = new MemoryStream();
    ms.Write(Create(("seed.txt", "seed"u8.ToArray())));
    var d = new UfsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("x/y.txt", "y"u8.ToArray())]);
    d.Remove(ms, ["x/y.txt"]);
    AssertClean(Fsck(ms.ToArray()));
    ms.Position = 0;
    using var r = new UfsReader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "seed.txt", "x" }));
  }
}
