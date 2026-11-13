using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class MountNamespaceResolverTests {

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  [Test]
  public void Ext4_IsParsedByCompressionWorkbenchEvenWhenHostCouldSupportIt() {
    var data = "always parse through our ext driver"u8.ToArray();
    using var image = Create(
      "Ext",
      [ArchiveInputInfo.InMemory("hello.txt", data)],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.Ordinal) {
          ["Version"] = "ext4",
        },
      });

    var probe = MountNamespaceResolver.Probe("Ext", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.FormatId, Is.EqualTo("Ext").IgnoreCase);
      Assert.That(probe.Layers, Is.EqualTo(new[] { "filesystem:Ext" }));
    });

    image.Position = 0;
    using var session = MountNamespaceResolver.Open(
      "Ext",
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    Assert.That(session.Profile.FormatId, Is.EqualTo("Ext").IgnoreCase);
    var file = session.Lookup(session.RootNodeId, "hello.txt");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(data));
  }

  [Test]
  public void Zip_ProjectsReadOnlyAndQualifiesStagedWrites() {
    var data = "mounted from zip"u8.ToArray();
    using var archive = Create("Zip", [ArchiveInputInfo.InMemory("nested/hello.txt", data)]);

    var probe = MountNamespaceResolver.Probe("Zip", archive);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.True);
      Assert.That(probe.Layers, Does.Contain("archive:Zip"));
      Assert.That(probe.Layers, Does.Contain("namespace:staged-worm-rebuild"));
    });

    archive.Position = 0;
    using var session = MountNamespaceResolver.Open(
      "Zip",
      archive,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var nested = session.Lookup(session.RootNodeId, "nested");
    Assert.That(nested, Is.Not.Null);
    var file = session.Lookup(nested!.Value, "hello.txt");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(data));
    Assert.Throws<NotSupportedException>(() => session.CreateFile(session.RootNodeId, "nope.txt"));
  }

  [TestCase("SevenZip")]
  [TestCase("Rar")]
  public void SupportedCompressedArchive_ExposesContentsAndQualifiesStagedWrites(string formatId) {
    var payload = "mountable compressed archive"u8.ToArray();
    using var archive = Create(formatId, [ArchiveInputInfo.InMemory("document.txt", payload)]);

    var probe = MountNamespaceResolver.Probe(formatId, archive);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.True);
      Assert.That(probe.Layers, Does.Contain($"archive:{formatId}"));
    });

    archive.Position = 0;
    using var namespaceSession = MountNamespaceResolver.Open(
      formatId,
      archive,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var file = namespaceSession.Lookup(namespaceSession.RootNodeId, "document.txt");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(namespaceSession, file!.Value), Is.EqualTo(payload));
    using var writable = MountNamespaceResolver.Open(
      formatId,
      archive,
      new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
    var nextFile = writable.CreateFile(writable.RootNodeId, "added.txt");
    using (var handle = writable.OpenFile(nextFile, FileAccess.Write))
      handle.Write(0, "new file"u8);
    writable.Flush();

    archive.Position = 0;
    using var readBack = MountNamespaceResolver.Open(
      formatId,
      archive,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var added = readBack.Lookup(readBack.RootNodeId, "added.txt");
    Assert.That(added, Is.Not.Null);
    Assert.That(ReadAll(readBack, added!.Value), Is.EqualTo("new file"u8.ToArray()));
  }

  [Test]
  public void WormOnlyPeaArchive_RebuildsWithMountedWrites() {
    var descriptor = FormatRegistry.GetById("Pea");
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());

    using var archive = Create("Pea", [ArchiveInputInfo.InMemory("existing.txt", "before"u8.ToArray())]);
    var probe = MountNamespaceResolver.Probe("Pea", archive);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMountWritable, Is.True);
      Assert.That(probe.Profile.MutationModel, Is.EqualTo(FilesystemMutationModel.WholeImageRebuild));
      Assert.That(probe.Layers, Does.Contain("namespace:staged-worm-rebuild"));
    });

    using (var mounted = MountNamespaceResolver.Open(
      "Pea", archive, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var node = mounted.CreateFile(mounted.RootNodeId, "created.txt");
      using (var file = mounted.OpenFile(node, FileAccess.Write)) {
        file.Write(0, "after"u8);
        file.Flush();
      }
      mounted.DeleteFile(mounted.RootNodeId, "existing.txt");
      mounted.Flush();
    }

    archive.Position = 0;
    using var verified = MountNamespaceResolver.Open(
      "Pea", archive, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(verified.Lookup(verified.RootNodeId, "existing.txt"), Is.Null);
    var created = verified.Lookup(verified.RootNodeId, "created.txt");
    Assert.That(created, Is.Not.Null);
    Assert.That(ReadAll(verified, created!.Value), Is.EqualTo("after"u8.ToArray()));
  }

  [Test]
  public void StagedZipMount_ChangesRemainUnpublishedUntilFlushAndPreserveNodeHandles() {
    var original = "first"u8.ToArray();
    using var archive = Create("Zip", [ArchiveInputInfo.InMemory("a.txt", original)]);
    var before = archive.ToArray();
    using var mounted = MountNamespaceResolver.Open(
      "Zip", archive, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
    var node = mounted.Lookup(mounted.RootNodeId, "a.txt");
    Assert.That(node, Is.Not.Null);

    using var handle = mounted.OpenFile(node!.Value, FileAccess.ReadWrite);
    handle.SetLength(0);
    handle.Write(0, "second"u8);
    mounted.Rename(mounted.RootNodeId, "a.txt", mounted.RootNodeId, "b.txt", replace: false);
    Assert.That(mounted.Lookup(mounted.RootNodeId, "b.txt"), Is.EqualTo(node));
    Assert.That(archive.ToArray(), Is.EqualTo(before));
    Assert.That(ReadAll(mounted, node.Value), Is.EqualTo("second"u8.ToArray()));

    mounted.Flush();
    Assert.That(archive.ToArray(), Is.Not.EqualTo(before));
    archive.Position = 0;
    using var verified = MountNamespaceResolver.Open(
      "Zip", archive, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(verified.Lookup(verified.RootNodeId, "a.txt"), Is.Null);
    var renamed = verified.Lookup(verified.RootNodeId, "b.txt");
    Assert.That(renamed, Is.Not.Null);
    Assert.That(ReadAll(verified, renamed!.Value), Is.EqualTo("second"u8.ToArray()));
  }

  [Test]
  public void Vhd_ResolvesGuestFatWithoutHostMounting() {
    var data = Enumerable.Range(0, 4096).Select(static i => (byte)(i * 37)).ToArray();
    using var image = Create("Vhd", [ArchiveInputInfo.InMemory("HELLO.TXT", data)]);

    var probe = MountNamespaceResolver.Probe("Vhd", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Layers, Does.Contain("container:Vhd"));
      Assert.That(probe.Layers, Does.Contain("block-device"));
      Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));
    });

    image.Position = 0;
    using var session = MountNamespaceResolver.Open(
      "Vhd",
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    Assert.That(session.Profile.FormatId, Is.EqualTo("Fat").IgnoreCase);
    var file = session.Lookup(session.RootNodeId, "HELLO.TXT");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(data));
  }

  [Test]
  public void RegisteredSingleFileFormat_ProjectsOriginalBytesWithoutInventingAWriter() {
    var descriptor = FormatRegistry.All.First(d =>
      !FormatRegistry.FilesystemFormatIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase) &&
      d is not IArchiveFormatOperations &&
      d is not IRandomAccessBlockDeviceProvider &&
      d is not IEncryptedBlockDeviceProvider);
    var data = Enumerable.Range(0, 8192).Select(static i => (byte)(i * 73)).ToArray();
    using var source = new MemoryStream(data, writable: true);

    var probe = MountNamespaceResolver.Probe(descriptor.Id, source);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.False);
      Assert.That(probe.Layers, Does.Contain("namespace:single-file-read-only"));
      Assert.That(source.Position, Is.Zero);
    });

    using var session = MountNamespaceResolver.Open(
      descriptor.Id,
      source,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true),
      sourceName: "movie.clip");
    var entry = session.Lookup(session.RootNodeId, "movie.clip");
    Assert.That(entry, Is.Not.Null);

    using var first = session.OpenFile(entry!.Value, FileAccess.Read);
    using var second = session.OpenFile(entry.Value, FileAccess.Read);
    var slice = new byte[257];
    Assert.That(first.Read(173, slice), Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(data.AsSpan(173, slice.Length).ToArray()));
    Assert.That(second.Read(data.Length - 5, slice), Is.EqualTo(5));
    Assert.Throws<NotSupportedException>(() => session.OpenFile(entry.Value, FileAccess.Write));
    Assert.That(source.ToArray(), Is.EqualTo(data));

    Assert.Throws<NotSupportedException>(() =>
      MountNamespaceResolver.Open(
        descriptor.Id,
        source,
        new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true),
        sourceName: "movie.clip"));
  }

  [Test]
  public void RawImg_ResolvesFatWithoutHostDiskMounting() {
    var payload = "raw disk mounted through FAT"u8.ToArray();
    using var image = Create("Fat", [ArchiveInputInfo.InMemory("HELLO.TXT", payload)]);

    var probe = MountNamespaceResolver.Probe(MountNamespaceResolver.RawDiskFormatId, image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Layers, Does.Contain("container:RawDisk"));
      Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));
    });

    using var session = MountNamespaceResolver.Open(
      MountNamespaceResolver.RawDiskFormatId,
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var file = session.Lookup(session.RootNodeId, "HELLO.TXT");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(payload));
  }

  [Test]
  public void RawImg_RejectsUnalignedOrUnrecognizedDiskBytes() {
    using var unaligned = new MemoryStream(new byte[513]);
    var probe = MountNamespaceResolver.Probe(MountNamespaceResolver.RawDiskFormatId, unaligned);
    Assert.That(probe.Profile.CanMount, Is.False);
    Assert.Throws<NotSupportedException>(() =>
      MountNamespaceResolver.Open(
        MountNamespaceResolver.RawDiskFormatId,
        unaligned,
        new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true)));

    using var unrecognized = new MemoryStream(new byte[512 * 8]);
    Assert.That(
      MountNamespaceResolver.Probe(MountNamespaceResolver.RawDiskFormatId, unrecognized).Profile.CanMount,
      Is.False);
  }

  [Test]
  public void PartitionBlockDevice_TranslatesBoundsAndWrites() {
    const int blockSize = 512;
    var bytes = new byte[8 * blockSize];
    for (var block = 0; block < 8; ++block)
      bytes.AsSpan(block * blockSize, blockSize).Fill((byte)block);

    using var stream = new MemoryStream(bytes, writable: true);
    using var disk = new StreamBlockDevice(stream, blockSize, writable: true, leaveOpen: true);
    using var partition = new PartitionBlockDevice(disk, firstBlock: 2, blockCount: 3, leaveOpen: true);

    var read = new byte[blockSize];
    Assert.That(partition.ReadBlocks(0, read), Is.EqualTo(1));
    Assert.That(read, Is.All.EqualTo((byte)2));

    var replacement = Enumerable.Repeat((byte)0xA5, blockSize).ToArray();
    partition.WriteBlocks(2, replacement);
    Assert.That(bytes.AsSpan(4 * blockSize, blockSize).SequenceEqual(replacement), Is.True);
    Assert.Throws<ArgumentOutOfRangeException>(() => partition.ReadBlocks(3, read));
  }

  [Test]
  public void MountBackendBoundary_ContainsParsedSessionRatherThanSourceImage() {
    var properties = typeof(FilesystemMountRequest).GetProperties()
      .Select(static property => property.Name)
      .Order(StringComparer.Ordinal)
      .ToArray();

    Assert.That(properties, Is.EqualTo(new[] {
      nameof(FilesystemMountRequest.Filesystem),
      nameof(FilesystemMountRequest.OwnsFilesystemSession),
      nameof(FilesystemMountRequest.Plan),
      nameof(FilesystemMountRequest.Target),
    }.Order(StringComparer.Ordinal).ToArray()));
    Assert.That(typeof(FilesystemMountRequest).GetProperty("ImagePath"), Is.Null);
    Assert.That(typeof(FilesystemMountRequest).GetProperty("Source"), Is.Null);
  }

  private static MemoryStream Create(
      string formatId,
      IReadOnlyList<ArchiveInputInfo> inputs,
      FormatCreateOptions? options = null) {
    var ops = FormatRegistry.GetArchiveOps(formatId)
      ?? throw new AssertionException($"{formatId} has no archive operations.");
    if (ops is not IArchiveCreatable creator)
      throw new AssertionException($"{formatId} is not creatable.");

    var result = new MemoryStream();
    creator.Create(result, inputs, options ?? new FormatCreateOptions { ForceCompress = true });
    result.Position = 0;
    return result;
  }

  private static byte[] ReadAll(IFilesystemSession session, FilesystemNodeId nodeId) {
    using var handle = session.OpenFile(nodeId, FileAccess.Read);
    if (handle.Length > int.MaxValue)
      throw new AssertionException("Test fixture unexpectedly exceeds array limits.");
    var result = new byte[(int)handle.Length];
    var offset = 0;
    while (offset < result.Length) {
      var read = handle.Read(offset, result.AsSpan(offset));
      if (read == 0) break;
      offset += read;
    }
    Assert.That(offset, Is.EqualTo(result.Length));
    return result;
  }
}
