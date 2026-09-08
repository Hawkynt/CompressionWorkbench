using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileSystem.MinixFs;
using FileSystem.MinixV2;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class MinixFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeMinixSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("MinixFs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasNativeReadinessProvider, Is.True);
  }

  [Test]
  public void UnifiedReaderDecodesRealV2InodeAndIndirectLayout() {
    var payload = Enumerable.Range(0, 400_000).Select(i => (byte)(i * 29 + 7)).ToArray();
    using var stream = new MemoryStream();
    using (var writer = new MinixV2Writer(stream, leaveOpen: true)) {
      writer.AddFile("large.bin", payload);
      writer.Finish();
    }

    stream.Position = 0;
    using var reader = new MinixFsReader(stream, leaveOpen: true);
    var entry = reader.Entries.Single(e => e.Name == "large.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(payload).AsCollection);
  }

  [Test]
  public void V3MountedWritesSurviveIndirectAllocationRenameUnlinkAndRemount() {
    using var image = BuildGrowableV3Image();
    var profile = FormatRegistry.ProbeFilesystem("MinixFs", image);
    Assert.That(profile.CanMountWritable, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.Direct));
    Assert.That(profile.Capabilities, Has.Flag(FilesystemDriverCapabilities.SparseFiles));
    Assert.That(profile.Capabilities, Has.Flag(FilesystemDriverCapabilities.HardLinks));
    Assert.That(profile.Capabilities, Has.Flag(FilesystemDriverCapabilities.SymbolicLinks));

    var payload = Enumerable.Range(0, 600_000).Select(i => (byte)(i * 31 + 11)).ToArray();
    FilesystemNodeId stableId;

    image.Position = 0;
    using (var session = FormatRegistry.OpenFilesystem(
      "MinixFs", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      FreeFixtureInodes(session);

      var docs = session.CreateDirectory(session.RootNodeId, "docs");
      var nested = session.CreateDirectory(docs, "nested");
      var file = session.CreateFile(nested, "large.bin");
      stableId = file;

      using var handle = session.OpenFile(file, FileAccess.ReadWrite);
      handle.Write(0, payload);
      handle.SetLength(700_000);
      handle.Write(699_900, "tail"u8);
      handle.SetLength(603_777);

      session.CreateHardLink(file, docs, "alias.bin");
      var symlink = session.CreateSymbolicLink(docs, "large.link", "nested/large.bin");
      Assert.That(session.ReadSymbolicLink(symlink), Is.EqualTo("nested/large.bin"));

      session.Rename(nested, "large.bin", session.RootNodeId, "moved.bin", replace: false);
      Assert.That(session.Stat(file).NodeId, Is.EqualTo(stableId));
      Assert.That(session.Lookup(session.RootNodeId, "moved.bin"), Is.EqualTo(stableId));
      Assert.That(session.Lookup(nested, "large.bin"), Is.Null);

      handle.Write(600_000, "after-rename"u8);
      handle.Flush();

      session.DeleteFile(docs, "alias.bin");
      session.DeleteFile(docs, "large.link");
      session.RemoveDirectory(docs, "nested");
      session.RemoveDirectory(session.RootNodeId, "docs");
      session.Flush();
    }

    image.Position = 0;
    using var remounted = FormatRegistry.OpenFilesystem(
      "MinixFs", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var moved = remounted.Lookup(remounted.RootNodeId, "moved.bin");
    Assert.That(moved, Is.Not.Null);
    using var read = remounted.OpenFile(moved!.Value, FileAccess.Read);
    var bytes = new byte[checked((int)read.Length)];
    Assert.That(read.Read(0, bytes), Is.EqualTo(bytes.Length));
    Assert.That(bytes.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload).AsCollection);
    Assert.That(bytes.AsSpan(600_000, "after-rename"u8.Length).ToArray(), Is.EqualTo("after-rename"u8.ToArray()).AsCollection);
    Assert.That(bytes.AsSpan(600_000 + "after-rename"u8.Length).ContainsAnyExcept((byte)0), Is.False);
  }

  [Test]
  public void UnlinkedOpenFileKeepsIdentityUntilLastHandleCloses() {
    using var image = BuildGrowableV3Image();
    image.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "MinixFs", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
    FreeFixtureInodes(session);

    var file = session.CreateFile(session.RootNodeId, "open.bin");
    using var handle = session.OpenFile(file, FileAccess.ReadWrite);
    handle.Write(0, "still-open"u8);
    session.DeleteFile(session.RootNodeId, "open.bin");
    Assert.That(session.Lookup(session.RootNodeId, "open.bin"), Is.Null);

    var buffer = new byte[10];
    Assert.That(handle.Read(0, buffer), Is.EqualTo(buffer.Length));
    Assert.That(buffer, Is.EqualTo("still-open"u8.ToArray()).AsCollection);
    Assert.That(session.Stat(file).LinkCount, Is.Zero);

    handle.Dispose();
    Assert.Throws<FileNotFoundException>(() => session.Stat(file));
  }

  private static MemoryStream BuildGrowableV3Image() {
    using var compact = new MemoryStream();
    using (var writer = new MinixFsWriter(compact, leaveOpen: true)) {
      writer.AddFile("seed.txt", "seed"u8.ToArray());
      for (var i = 0; i < 64; ++i)
        writer.AddFile($"free{i:D2}", []);
      writer.Finish();
    }

    var bytes = compact.ToArray();
    const int additionalZones = 1500;
    var oldZones = checked(bytes.Length / 1024);
    var newZones = checked(oldZones + additionalZones);
    Array.Resize(ref bytes, checked(newZones * 1024));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024 + 16, 4), 16 * 1024 * 1024); // s_max_size
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024 + 20, 4), (uint)newZones);     // s_zones
    return new MemoryStream(bytes, writable: true);
  }

  private static void FreeFixtureInodes(IFilesystemSession session) {
    for (var i = 0; i < 64; ++i)
      session.DeleteFile(session.RootNodeId, $"free{i:D2}");
  }
}