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

  [Test]
  public void CloneCdSourceMountListsAndReadsRealSiblingImgAndSubFiles() {
    var directory = Path.Combine(Path.GetTempPath(), $"cwb-ccd-mount-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var stem = Path.Combine(directory, "disc");
    var ccd = stem + ".ccd";
    var img = stem + ".img";
    var sub = stem + ".sub";
    var rawImage = Enumerable.Range(0, 2352).Select(static index => (byte)index).ToArray();
    var subchannel = Enumerable.Range(0, 96).Select(static index => (byte)(255 - index)).ToArray();
    File.WriteAllText(ccd, "[CloneCD]\nVersion=3\n[Disc]\nTocEntries=1\nSessions=1\n");
    File.WriteAllBytes(img, rawImage);
    File.WriteAllBytes(sub, subchannel);

    try {
      using var source = new FileStream(ccd, FileMode.Open, FileAccess.Read, FileShare.Read);
      var probe = MountNamespaceResolver.Probe("CloneCd", source);
      Assert.That(probe.Profile.CanMount, Is.True);

      using var session = MountNamespaceResolver.Open(
        "CloneCd", source, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
      Assert.Multiple(() => {
        Assert.That(session.Enumerate(session.RootNodeId).Select(static entry => entry.Name),
          Is.EquivalentTo(new[] { "FULL.ccd", "metadata.ini", "disc.img", "disc.sub" }));
        Assert.That(ReadAll(session, "disc.img"), Is.EqualTo(rawImage));
        Assert.That(ReadAll(session, "disc.sub"), Is.EqualTo(subchannel));
        Assert.That(ReadAll(session, "metadata.ini").Length, Is.GreaterThan(0),
          "Generated metadata must not be hidden behind a zero-length stat.");
      });
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public void VmdkNativeSparseBlockMountPersistsChangesThroughInnerFatDriver() {
    var fat = new FileSystem.Fat.FatWriter();
    fat.AddFile("KEEP.TXT", "persisted originally"u8.ToArray());
    var vmdk = new FileFormat.Vmdk.VmdkWriter();
    vmdk.SetDiskData(fat.Build());

    using var image = new MemoryStream(vmdk.Build());
    var probe = MountNamespaceResolver.Probe("Vmdk", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True, string.Join("; ", probe.Profile.Limitations));
      Assert.That(probe.Profile.CanMountWritable, Is.True,
        "The native FAT driver and writable monolithic-sparse VMDK mapping must both qualify.");
      Assert.That(probe.Layers, Does.Contain("container:Vmdk"));
      Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));
    });

    image.Position = 0;
    using (var session = MountNamespaceResolver.Open(
      "Vmdk", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var added = session.CreateFile(session.RootNodeId, "ADDED.TXT");
      using (var file = session.OpenFile(added, FileAccess.ReadWrite)) {
        file.Write(0, "written through vmdk"u8);
        file.Flush();
      }
      session.Flush();
    }

    image.Position = 0;
    using var reopened = MountNamespaceResolver.Open(
      "Vmdk", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(ReadAll(reopened, "KEEP.TXT"), Is.EqualTo("persisted originally"u8.ToArray()));
    Assert.That(ReadAll(reopened, "ADDED.TXT"), Is.EqualTo("written through vmdk"u8.ToArray()));
  }

  [Test]
  public void UnknownD90ImageRemainsAccessibleOnlyAsAnExplicitRawReadOnlyFile() {
    var payload = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    using var source = new MemoryStream(payload, writable: false);
    var probe = MountNamespaceResolver.Probe("Unknown", source);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.False);
      Assert.That(probe.Layers, Is.EqualTo(new[] { "format:Unknown", "namespace:opaque-read-only" }));
    });

    using var session = MountNamespaceResolver.Open(
      "Unknown", source,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true),
      sourceName: "disk.d90");
    Assert.That(ReadAll(session, "disk.d90"), Is.EqualTo(payload));
    Assert.Throws<NotSupportedException>(() => MountNamespaceResolver.Open(
      "Unknown", source, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
    Assert.Throws<KeyNotFoundException>(() => MountNamespaceResolver.Probe("NotRegistered", source));
  }

  [Test]
  public void PngUsesItsExistingStructuredReadOnlyPseudoArchiveRatherThanOpaqueFallback() {
    // Tiny valid PNG. The own PNG descriptor, not the host's image decoder,
    // projects FULL.png plus independently exposed chunks and metadata.
    var png = Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO/a9xAAAAAASUVORK5CYII=");
    using var source = new MemoryStream(png, writable: false);
    var probe = MountNamespaceResolver.Probe("Png", source);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.False);
      Assert.That(probe.Layers.Last(), Is.EqualTo("namespace:derived-read-only"));
    });

    using var session = MountNamespaceResolver.Open(
      "Png", source, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(ReadAll(session, "FULL.png"), Is.EqualTo(png));
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
