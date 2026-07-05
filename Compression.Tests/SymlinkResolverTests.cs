using Compression.Registry;

namespace Compression.Tests.Symlink;

/// <summary>
/// Behavioural unit tests for <see cref="SymlinkResolver"/> — the cross-filesystem
/// "report the pointed-to file's size, not the link's own size" policy. Each test
/// builds a synthetic listing (as any reader's <c>List</c> produces) and asserts
/// the resolved <see cref="ArchiveEntryInfo.TargetSize"/> follows the documented
/// policy: relative link to a regular file in the listing resolves; absolute,
/// escaping, dangling, directory, and cyclic targets stay unknown.
/// </summary>
[TestFixture]
public class SymlinkResolverTests {

  private static ArchiveEntryInfo File(string name, long size)
    => new(0, name, size, size, "Stored", false, false, null);

  private static ArchiveEntryInfo Dir(string name)
    => new(0, name, 0, 0, "Stored", true, false, null);

  private static ArchiveEntryInfo Link(string name, string target)
    => new(0, name, target.Length, target.Length, "Stored", false, false, null,
        IsSymlink: true, LinkTarget: target);

  private static ArchiveEntryInfo Resolved(List<ArchiveEntryInfo> input, string name)
    => SymlinkResolver.Resolve(input).Single(e => e.Name == name);

  [Test]
  public void RelativeLinkToRegularFileInSameDirectory_ResolvesToFileSize() {
    var entries = new List<ArchiveEntryInfo> {
      File("target.txt", 4096),
      Link("fast", "target.txt"),
    };
    Assert.That(Resolved(entries, "fast").TargetSize, Is.EqualTo(4096));
  }

  [Test]
  public void LinkOwnSize_StaysTargetPathLength_NotTargetSize() {
    var entries = new List<ArchiveEntryInfo> {
      File("target.txt", 4096),
      Link("fast", "target.txt"),
    };
    var link = Resolved(entries, "fast");
    Assert.That(link.OriginalSize, Is.EqualTo("target.txt".Length),
      "the link's own size must remain the target-path byte length");
    Assert.That(link.TargetSize, Is.EqualTo(4096),
      "the resolved target size is surfaced separately");
  }

  [Test]
  public void RelativeLinkWithParentTraversal_Resolves() {
    var entries = new List<ArchiveEntryInfo> {
      Dir("a"),
      Dir("a/b"),
      File("a/data.bin", 1234),
      Link("a/b/up", "../data.bin"),
    };
    Assert.That(Resolved(entries, "a/b/up").TargetSize, Is.EqualTo(1234));
  }

  [Test]
  public void ChainedLinks_FollowToFinalFile() {
    var entries = new List<ArchiveEntryInfo> {
      File("real.dat", 777),
      Link("mid", "real.dat"),
      Link("head", "mid"),
    };
    Assert.That(Resolved(entries, "head").TargetSize, Is.EqualTo(777));
  }

  [Test]
  public void AbsoluteTarget_LeavesTargetSizeNull() {
    var entries = new List<ArchiveEntryInfo> {
      File("etc/hosts", 88),
      Link("hosts", "/etc/hosts"),
    };
    Assert.That(Resolved(entries, "hosts").TargetSize, Is.Null);
  }

  [Test]
  public void TargetEscapingRoot_LeavesTargetSizeNull() {
    var entries = new List<ArchiveEntryInfo> {
      Link("bad", "../../outside.txt"),
    };
    Assert.That(Resolved(entries, "bad").TargetSize, Is.Null);
  }

  [Test]
  public void DanglingTarget_LeavesTargetSizeNull() {
    var entries = new List<ArchiveEntryInfo> {
      Link("nowhere", "missing.txt"),
    };
    Assert.That(Resolved(entries, "nowhere").TargetSize, Is.Null);
  }

  [Test]
  public void DirectoryTarget_LeavesTargetSizeNull() {
    var entries = new List<ArchiveEntryInfo> {
      Dir("somedir"),
      Link("d", "somedir"),
    };
    Assert.That(Resolved(entries, "d").TargetSize, Is.Null);
  }

  [Test]
  public void CyclicLinks_LeaveTargetSizeNull() {
    var entries = new List<ArchiveEntryInfo> {
      Link("a", "b"),
      Link("b", "a"),
    };
    var resolved = SymlinkResolver.Resolve(entries);
    Assert.That(resolved.Single(e => e.Name == "a").TargetSize, Is.Null);
    Assert.That(resolved.Single(e => e.Name == "b").TargetSize, Is.Null);
  }

  [Test]
  public void SelfReferentialLink_LeavesTargetSizeNull() {
    var entries = new List<ArchiveEntryInfo> {
      Link("loop", "loop"),
    };
    Assert.That(Resolved(entries, "loop").TargetSize, Is.Null);
  }

  [Test]
  public void NonLinkEntries_ArePassedThroughUnchanged() {
    var entries = new List<ArchiveEntryInfo> {
      File("plain.txt", 10),
      Dir("folder"),
    };
    var resolved = SymlinkResolver.Resolve(entries);
    Assert.That(resolved.Single(e => e.Name == "plain.txt").TargetSize, Is.Null);
    Assert.That(resolved.Single(e => e.Name == "plain.txt").IsSymlink, Is.False);
  }

  [Test]
  public void DotSegmentsInTarget_AreNormalized() {
    var entries = new List<ArchiveEntryInfo> {
      Dir("a"),
      File("a/x.bin", 55),
      Link("a/self", "./x.bin"),
    };
    Assert.That(Resolved(entries, "a/self").TargetSize, Is.EqualTo(55));
  }
}
