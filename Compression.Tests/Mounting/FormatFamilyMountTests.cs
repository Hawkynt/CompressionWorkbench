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
  public void ZipNamedFileUsesVerifiedJournalledInPlaceMutationAndLeavesNoUndoFile() {
    var folder = Path.Combine(Path.GetTempPath(), $"cwb-zip-mount-journal-{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "image.zip");
    try {
      var creator = (IArchiveCreatable)FormatRegistry.GetById("Zip")!;
      using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        creator.Create(destination, [
          ArchiveInputInfo.InMemory("kept.txt", "still here"u8.ToArray()),
          ArchiveInputInfo.InMemory("removed.txt", "bye"u8.ToArray()),
        ], new FormatCreateOptions());

      using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
        using var mount = MountNamespaceResolver.Open(
          "Zip", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
        var node = mount.CreateFile(mount.RootNodeId, "created.txt");
        using (var handle = mount.OpenFile(node, FileAccess.ReadWrite))
          handle.Write(0, "journalled"u8);
        mount.DeleteFile(mount.RootNodeId, "removed.txt");
        mount.Flush();

        mount.Rename(mount.RootNodeId, "created.txt", mount.RootNodeId, "renamed.txt", replace: false);
        using (var handle = mount.OpenFile(node, FileAccess.ReadWrite))
          handle.Write(0, "J"u8);
        mount.Flush();
      }

      Assert.That(File.Exists(JournaledArchiveWriteback.JournalPath(path)), Is.False);
      using var reopened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var filesystem = MountNamespaceResolver.Open(
        "Zip", reopened, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
      Assert.Multiple(() => {
        Assert.That(filesystem.Lookup(filesystem.RootNodeId, "removed.txt"), Is.Null);
        Assert.That(filesystem.Lookup(filesystem.RootNodeId, "created.txt"), Is.Null);
        Assert.That(ReadAll(filesystem, "renamed.txt"), Is.EqualTo("Journalled"u8.ToArray()));
        Assert.That(ReadAll(filesystem, "kept.txt"), Is.EqualTo("still here"u8.ToArray()));
      });
    } finally {
      Directory.Delete(folder, recursive: true);
    }
  }

  [Test]
  public void ZipWithSixtyFiveMiBUncompressedMemberMountsWritableUsingDiskSpooling() {
    const int sourceMiB = 65;
    using var image = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(
      image, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true)) {
      var entry = zip.CreateEntry("large.bin", System.IO.Compression.CompressionLevel.Fastest);
      using var data = entry.Open();
      var oneMiB = new byte[1024 * 1024];
      for (var i = 0; i < sourceMiB; ++i)
        data.Write(oneMiB);
    }

    image.Position = 0;
    var profile = MountNamespaceResolver.Probe("Zip", image);
    Assert.Multiple(() => {
      Assert.That(profile.Profile.CanMountWritable, Is.True,
        string.Join("; ", profile.Profile.Limitations));
      Assert.That(profile.Profile.Limitations, Has.Some.Contains("disk-spooled"));
    });

    image.Position = 0;
    using (var mount = MountNamespaceResolver.Open(
      "Zip", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var initial = mount.Lookup(mount.RootNodeId, "large.bin")!.Value;
      using (var handle = mount.OpenFile(initial, FileAccess.Read)) {
        Assert.That(handle.Length, Is.EqualTo(65L * 1024 * 1024));
        Span<byte> read = stackalloc byte[8];
        Assert.That(handle.Read(handle.Length - 8, read), Is.EqualTo(8));
        Assert.That(read.ToArray(), Is.All.Zero);
      }

      var added = mount.CreateFile(mount.RootNodeId, "small.txt");
      using (var handle = mount.OpenFile(added, FileAccess.ReadWrite))
        handle.Write(0, "persisted"u8);
      mount.Flush();
    }

    image.Position = 0;
    using var reopened = MountNamespaceResolver.Open(
      "Zip", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(ReadAll(reopened, "small.txt"), Is.EqualTo("persisted"u8.ToArray()));
    var large = reopened.Lookup(reopened.RootNodeId, "large.bin")!.Value;
    using var reread = reopened.OpenFile(large, FileAccess.Read);
    Assert.That(reread.Length, Is.EqualTo(65L * 1024 * 1024));
    Span<byte> end = stackalloc byte[8];
    Assert.That(reread.Read(reread.Length - 8, end), Is.EqualTo(8));
    Assert.That(end.ToArray(), Is.All.Zero);
  }

  [Test]
  public void WormOnlyWimMountStagesFullCopyAndPersistsRepeatedWrites() {
    var descriptor = FormatRegistry.GetById("Wim")!;
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    });

    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      [
        ArchiveInputInfo.InMemory("keep.bin", "unmodified"u8.ToArray()),
        ArchiveInputInfo.InMemory("remove.bin", "to be removed"u8.ToArray()),
      ],
      new FormatCreateOptions());
    var sourceBytes = image.ToArray();

    image.Position = 0;
    var probe = MountNamespaceResolver.Probe("Wim", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMountWritable, Is.True, string.Join("; ", probe.Profile.Limitations));
      Assert.That(probe.Profile.MutationModel, Is.EqualTo(FilesystemMutationModel.WholeImageRebuild));
      Assert.That(probe.Layers.Last(), Is.EqualTo("namespace:qualified-worm-rebuild"));
      Assert.That(probe.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.CreateDirectory), Is.False);
    });

    image.Position = 0;
    using (var session = MountNamespaceResolver.Open(
      "Wim", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      Assert.Throws<NotSupportedException>(() => session.CreateDirectory(session.RootNodeId, "empty"));

      var created = session.CreateFile(session.RootNodeId, "new.bin");
      using (var handle = session.OpenFile(created, FileAccess.ReadWrite)) {
        handle.Write(0, "abcdefgh"u8);
        handle.SetLength(6);
      }
      Assert.That(image.ToArray(), Is.EqualTo(sourceBytes),
        "All file changes must remain staged until the write-back flush.");
      session.Flush();

      var afterFirstFlush = image.ToArray();
      Assert.That(afterFirstFlush, Is.Not.EqualTo(sourceBytes));

      session.Rename(session.RootNodeId, "new.bin", session.RootNodeId, "renamed.bin", replace: false);
      session.DeleteFile(session.RootNodeId, "remove.bin");
      using (var handle = session.OpenFile(created, FileAccess.ReadWrite))
        handle.Write(2, "XY"u8);
      Assert.That(image.ToArray(), Is.EqualTo(afterFirstFlush));
      session.Flush();
    }

    image.Position = 0;
    using var reread = MountNamespaceResolver.Open(
      "Wim", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.Multiple(() => {
      Assert.That(reread.Lookup(reread.RootNodeId, "new.bin"), Is.Null);
      Assert.That(reread.Lookup(reread.RootNodeId, "remove.bin"), Is.Null);
      Assert.That(ReadAll(reread, "renamed.bin"), Is.EqualTo("abXYef"u8.ToArray()));
      Assert.That(ReadAll(reread, "keep.bin"), Is.EqualTo("unmodified"u8.ToArray()));
    });
  }

  [Test]
  public void CompoundTarGzipUsesWormRebuildForMountedWrites() {
    var descriptor = FormatRegistry.GetById("TarGz")!;
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());

    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory("folder/old.txt", "old"u8.ToArray())],
      new FormatCreateOptions());
    image.Position = 0;

    var probe = MountNamespaceResolver.Probe("TarGz", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMountWritable, Is.True, string.Join("; ", probe.Profile.Limitations));
      Assert.That(probe.Layers.Last(), Is.EqualTo("namespace:qualified-worm-rebuild"));
      Assert.That(probe.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.CreateDirectory), Is.True);
    });

    image.Position = 0;
    using (var session = MountNamespaceResolver.Open(
      "TarGz", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var folder = session.Lookup(session.RootNodeId, "folder")!.Value;
      var created = session.CreateFile(folder, "new.txt");
      using (var handle = session.OpenFile(created, FileAccess.ReadWrite))
        handle.Write(0, "new"u8);
      session.DeleteFile(folder, "old.txt");
      session.Flush();
    }

    image.Position = 0;
    using var reopened = MountNamespaceResolver.Open(
      "TarGz", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var directory = reopened.Lookup(reopened.RootNodeId, "folder")!.Value;
    Assert.Multiple(() => {
      Assert.That(reopened.Lookup(directory, "old.txt"), Is.Null);
      Assert.That(reopened.Lookup(directory, "new.txt"), Is.Not.Null);
    });
    var child = reopened.Lookup(directory, "new.txt")!.Value;
    using var reader = reopened.OpenFile(child, FileAccess.Read);
    var bytes = new byte[checked((int)reader.Length)];
    Assert.That(reader.Read(0, bytes), Is.EqualTo(bytes.Length));
    Assert.That(bytes, Is.EqualTo("new"u8.ToArray()));
  }

  [Test]
  public void WormOnlyWimRequiresWritableBackingForMountedMutations() {
    var writer = (IArchiveCreatable)FormatRegistry.GetById("Wim")!;
    using var generated = new MemoryStream();
    writer.Create(generated, [ArchiveInputInfo.InMemory("read.txt", "data"u8.ToArray())],
      new FormatCreateOptions());
    using var readOnlySource = new MemoryStream(generated.ToArray(), writable: false);

    var profile = MountNamespaceResolver.Probe("Wim", readOnlySource);
    Assert.That(profile.Profile.CanMount, Is.True);
    Assert.That(profile.Profile.CanMountWritable, Is.True,
      "Capability describes the format; the mount planner separately requires a writable source.");
    Assert.Throws<NotSupportedException>(() =>
      MountNamespaceResolver.Open("Wim", readOnlySource,
        new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));

    readOnlySource.Position = 0;
    using var readOnly = MountNamespaceResolver.Open("Wim", readOnlySource,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(ReadAll(readOnly, "read.txt"), Is.EqualTo("data"u8.ToArray()));
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

    using var image = new MemoryStream();
    image.Write(vmdk.Build());
    image.Position = 0;
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
