using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class FormatFamilyMountTests {
  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  [TestCase("Zip")]
  [TestCase("SevenZip")]
  [TestCase("Rar")]
  public void QualifiedArchiveMountWritesSurviveRepeatedFlushRenameAndReopen(string formatId) {
    var descriptor = FormatRegistry.GetById(formatId)!;
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());

    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      [
        ArchiveInputInfo.InMemory("keep.txt", "kept unchanged"u8.ToArray()),
        ArchiveInputInfo.InMemory("delete.txt", "remove me"u8.ToArray()),
      ],
      new FormatCreateOptions());

    image.Position = 0;
    var probe = MountNamespaceResolver.Probe(formatId, image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMountWritable, Is.True,
        $"Qualified {formatId} archive should support transactional write-back.");
      Assert.That(probe.Layers.Last(), Is.EqualTo("namespace:qualified-write-back"));
    });

    image.Position = 0;
    using (var session = MountNamespaceResolver.Open(
      formatId, image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      Assert.That(session.CreateFile(session.RootNodeId, "created.bin"), Is.Not.EqualTo(default(FilesystemNodeId)));
      var created = session.Lookup(session.RootNodeId, "created.bin")!.Value;
      using (var file = session.OpenFile(created, FileAccess.ReadWrite)) {
        file.Write(0, "abcdefghijkl"u8);
        file.SetLength(8);
      }
      session.Flush();

      Assert.That(session.Lookup(session.RootNodeId, "created.bin"), Is.EqualTo(created));
      session.Rename(session.RootNodeId, "created.bin", session.RootNodeId, "renamed.bin", replace: false);
      Assert.That(session.Lookup(session.RootNodeId, "renamed.bin"), Is.EqualTo(created),
        "Rename must not invalidate open node identities.");
      session.DeleteFile(session.RootNodeId, "delete.txt");
      using (var file = session.OpenFile(created, FileAccess.ReadWrite))
        file.Write(2, "XY"u8);
      session.Flush();

      Assert.Multiple(() => {
        Assert.That(session.Lookup(session.RootNodeId, "created.bin"), Is.Null);
        Assert.That(session.Lookup(session.RootNodeId, "delete.txt"), Is.Null);
        Assert.That(session.Lookup(session.RootNodeId, "renamed.bin"), Is.EqualTo(created));
      });
    }

    image.Position = 0;
    using var reopened = MountNamespaceResolver.Open(
      formatId, image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.Multiple(() => {
      Assert.That(reopened.Lookup(reopened.RootNodeId, "delete.txt"), Is.Null);
      Assert.That(reopened.Lookup(reopened.RootNodeId, "created.bin"), Is.Null);
      Assert.That(reopened.Lookup(reopened.RootNodeId, "renamed.bin"), Is.Not.Null);
    });
    Assert.That(ReadAll(reopened, "renamed.bin"), Is.EqualTo("abXYefgh"u8.ToArray()));
    Assert.That(ReadAll(reopened, "keep.txt"), Is.EqualTo("kept unchanged"u8.ToArray()));
  }

  [Test]
  public void ArchiveProbeRefusesWritesWhenCredentialsWouldMakeRebuildUnsafe() {
    using var image = new MemoryStream();
    var descriptor = (IArchiveCreatable)FormatRegistry.GetById("Zip")!;
    descriptor.Create(image,
      [ArchiveInputInfo.InMemory("safe.txt", "untouched"u8.ToArray())],
      new FormatCreateOptions());

    image.Position = 0;
    var probe = MountNamespaceResolver.Probe("Zip", image, password: "not-needed");
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.False);
    });
    Assert.Throws<NotSupportedException>(() => MountNamespaceResolver.Open(
      "Zip", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true), password: "not-needed"));
  }

  [Test]
  public void StreamOnlyDescriptorMountsOriginalBytesWithoutInventingAnArchive() {
    var original = "opaque gzip-like contents are still accessible"u8.ToArray();
    using var image = new MemoryStream(original, writable: false);

    var probe = MountNamespaceResolver.Probe("Gzip", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.False);
      Assert.That(probe.Layers, Is.EqualTo(new[] { "format:Gzip", "namespace:opaque-read-only" }));
    });

    using var session = MountNamespaceResolver.Open(
      "Gzip", image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true),
      sourceName: "example.gz");
    Assert.Multiple(() => {
      Assert.That(session.Enumerate(session.RootNodeId).Select(static entry => entry.Name), Is.EqualTo(new[] { "example.gz" }));
      Assert.That(ReadAll(session, "example.gz"), Is.EqualTo(original));
      Assert.Throws<NotSupportedException>(() => session.CreateFile(session.RootNodeId, "not-allowed"));
    });
  }

  private static byte[] ReadAll(IFilesystemSession session, string name) {
    var node = session.Lookup(session.RootNodeId, name);
    Assert.That(node, Is.Not.Null);
    using var file = session.OpenFile(node!.Value, FileAccess.Read);
    var bytes = new byte[checked((int)file.Length)];
    var offset = 0;
    while (offset < bytes.Length) {
      var count = file.Read(offset, bytes.AsSpan(offset));
      Assert.That(count, Is.GreaterThan(0), "Mounted entry returned early EOF.");
      offset += count;
    }
    return bytes;
  }
}
