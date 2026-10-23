using Compression.Registry;
using FileSystem.Zfs;
using static Compression.Tests.Zfs.ZfsReferencePools;

namespace Compression.Tests.Zfs;

[TestFixture]
public sealed class ZfsFilesystemDriverTests {
  [Test, Category("HappyPath"), Category("Oracle")]
  public void NativeSession_ReadsAPositionalSliceOfARealPoolFile() {
    var adapter = new ZfsFilesystemDriverAdapter();
    using var image = Open("cwbmain");
    var profile = adapter.ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.StableNodeIds), Is.True);

    using var session = adapter.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var dataset = session.Lookup(session.RootNodeId, "c-zstd")!.Value;
    var file = session.Lookup(dataset, "text.txt")!.Value;
    using var handle = session.OpenFile(file, FileAccess.Read);

    using var reader = new ZfsReader(Open("cwbmain"));
    var expected = reader.Extract(reader.Entries.Single(e => e.Name == "c-zstd/text.txt"));
    Assert.That(Sha256Of(expected), Is.EqualTo(Manifest("cwbmain").Single(m => m.Path == "c-zstd/text.txt").Sha256));
    Assert.That(handle.Length, Is.EqualTo(expected.Length));
    var slice = new byte[733];
    Assert.That(handle.Read(150_001, slice), Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(expected.AsSpan(150_001, slice.Length).ToArray()));
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void NativeSession_HardLinksShareANodeAndLinksResolve() {
    using var image = Open("cwbmain");
    using var session = new ZfsFilesystemDriverAdapter()
      .OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var text = session.Lookup(session.RootNodeId, "text.txt")!.Value;
    var hard = session.Lookup(session.RootNodeId, "hardlink.txt")!.Value;
    Assert.That(hard, Is.EqualTo(text));
    Assert.That(session.Stat(text).LinkCount, Is.EqualTo(2u));
    var link = session.Lookup(session.RootNodeId, "link")!.Value;
    Assert.That(session.ReadSymbolicLink(link), Is.EqualTo("text.txt"));
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void MultiStream_RaidZ1FromAStreamSet() {
    var sources = new FilesystemStreamSet(Enumerable.Range(0, 3)
      .Select(i => new FilesystemStreamSource($"cwbraidz1-{i}", Open($"cwbraidz1-{i}"), FilesystemSourceRole.Data)));
    var adapter = new ZfsFilesystemDriverAdapter();
    Assert.That(adapter.ProbeFilesystem(sources).CanMount, Is.True);
    using var session = adapter.OpenFilesystem(sources, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var big = session.Lookup(session.RootNodeId, "big.txt")!.Value;
    using var handle = session.OpenFile(big, FileAccess.Read);
    Assert.That(handle.Length, Is.EqualTo(Manifest("cwbraidz1").Single(m => m.Path == "big.txt").Size));
  }

  [Test, Category("ErrorHandling")]
  public void NativeSession_RefusesWritableMount() {
    using var image = Open("cwbv28");
    Assert.Throws<NotSupportedException>(() =>
      new ZfsFilesystemDriverAdapter().OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }

  [Test, Category("ErrorHandling")]
  public void Probe_GarbageIsNotMountable() {
    var profile = new ZfsFilesystemDriverAdapter().ProbeFilesystem(new MemoryStream(new byte[2 * 1024 * 1024]));
    Assert.That(profile.CanMount, Is.False);
  }
}
