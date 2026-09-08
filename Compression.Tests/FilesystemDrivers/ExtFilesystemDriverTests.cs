using Compression.Lib;
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class ExtFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeExtSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Ext");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasNativeReadinessProvider, Is.True);
  }

  [Test]
  public void NativeSessionProvidesStableInodeIdentityAndPositionalReads() {
    var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 31)).ToArray();
    var writer = new ExtWriter();
    writer.AddFile("dir/file.bin", payload);
    var image = writer.Build();

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Ext", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Ext", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "file.bin");
    Assert.That(file, Is.Not.Null);

    var firstStat = session.Stat(file!.Value);
    var secondStat = session.Stat(file.Value);
    Assert.That(secondStat.NodeId, Is.EqualTo(firstStat.NodeId));
    Assert.That(firstStat.NodeId.Value, Is.GreaterThan(0));
    Assert.That(firstStat.Size, Is.EqualTo(payload.Length));

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[777];
    var read = handle.Read(12_345, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(12_345, slice.Length).ToArray()));
  }

  [Test]
  public void ClassicExt2MountedWritesSurviveRenameDeleteFlushAndRemount() {
    var writer = new ExtWriter();
    writer.AddFile("seed.txt", "seed"u8.ToArray());
    var image = writer.Build(
      blockSize: 1024,
      totalBlocks: 8000,
      ExtWriter.ExtVersion.Ext2,
      journal: false,
      volumeLabel: "cwbmount",
      inodeSize: 256);

    using var stream = new MemoryStream(image, writable: true);
    var profile = FormatRegistry.ProbeFilesystem("Ext", stream);
    Assert.That(profile.CanMountWritable, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.Direct));
    Assert.That(profile.Capabilities, Has.Flag(FilesystemDriverCapabilities.SparseFiles));

    FilesystemNodeId movedId;
    var prefix = Enumerable.Range(0, 18_000).Select(i => (byte)(i * 17 + 3)).ToArray();
    var tail = "tail-after-rename"u8.ToArray();

    stream.Position = 0;
    using (var session = FormatRegistry.OpenFilesystem(
      "Ext", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var docs = session.CreateDirectory(session.RootNodeId, "docs");
      var file = session.CreateFile(docs, "data.bin");
      using var handle = session.OpenFile(file, FileAccess.ReadWrite);
      handle.Write(0, prefix);
      handle.Write(25_000, "sparse-tail"u8);
      handle.SetLength(18_321);

      session.CreateHardLink(file, docs, "alias.bin");
      var link = session.CreateSymbolicLink(docs, "data.link", "data.bin");
      Assert.That(session.ReadSymbolicLink(link), Is.EqualTo("data.bin"));

      session.Rename(docs, "data.bin", session.RootNodeId, "moved.bin", replace: false);
      movedId = file;
      handle.Write(prefix.Length, tail);
      handle.Flush();

      Assert.That(session.Stat(movedId).NodeId, Is.EqualTo(file));
      Assert.That(session.Lookup(session.RootNodeId, "moved.bin"), Is.EqualTo(file));
      Assert.That(session.Lookup(docs, "data.bin"), Is.Null);

      session.DeleteFile(docs, "alias.bin");
      session.DeleteFile(docs, "data.link");
      session.RemoveDirectory(session.RootNodeId, "docs");
      session.DeleteFile(session.RootNodeId, "seed.txt");
      session.Flush();
    }

    stream.Position = 0;
    var after = FormatRegistry.ProbeFilesystem("Ext", stream);
    Assert.That(after.CanMountWritable, Is.True, string.Join("; ", after.Limitations));

    stream.Position = 0;
    using var remounted = FormatRegistry.OpenFilesystem(
      "Ext", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var moved = remounted.Lookup(remounted.RootNodeId, "moved.bin");
    Assert.That(moved, Is.EqualTo(movedId));
    Assert.That(remounted.Lookup(remounted.RootNodeId, "docs"), Is.Null);
    Assert.That(remounted.Lookup(remounted.RootNodeId, "seed.txt"), Is.Null);

    using var read = remounted.OpenFile(moved!.Value, FileAccess.Read);
    var contents = new byte[checked((int)read.Length)];
    Assert.That(read.Read(0, contents), Is.EqualTo(contents.Length));
    Assert.That(contents.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix));
    Assert.That(contents.AsSpan(prefix.Length, tail.Length).ToArray(), Is.EqualTo(tail));
    Assert.That(contents.AsSpan(prefix.Length + tail.Length).ContainsAnyExcept((byte)0), Is.False);
  }

  [Test]
  public void JournaledExtProfileStaysReadOnly() {
    var writer = new ExtWriter();
    writer.AddFile("x", "data"u8.ToArray());
    using var stream = new MemoryStream(
      writer.Build(1024, 8000, ExtWriter.ExtVersion.Ext3, journal: true, volumeLabel: "journal", inodeSize: 256),
      writable: true);

    var profile = FormatRegistry.ProbeFilesystem("Ext", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(profile.Limitations, Has.Some.Contains("JBD").IgnoreCase);
    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Ext", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}