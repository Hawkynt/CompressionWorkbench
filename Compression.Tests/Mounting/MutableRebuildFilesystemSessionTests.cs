#pragma warning disable CS1591
using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class MutableRebuildFilesystemSessionTests {
  private static readonly byte[] InitialPayload = Enumerable.Range(0, 512).Select(static value => (byte)value).ToArray();
  private static readonly byte[] MutationPayload = Enumerable.Range(0, 1536).Select(static value => (byte)(value * 17)).ToArray();

  [Test]
  [Category("Mounting")]
  public void RomFsCreateWriteTruncateRenameDeleteFlushReopenPersistsExactly() {
    FormatRegistration.EnsureInitialized();
    var operations = FormatRegistry.GetArchiveOps("RomFs");
    Assert.That(operations, Is.InstanceOf<IArchiveCreatable>());

    using var image = new MemoryStream();
    ((IArchiveCreatable)operations!).Create(
      image,
      [
        ArchiveInputInfo.InMemory("KEEP.BIN", InitialPayload),
        ArchiveInputInfo.InMemory("DELETE.BIN", "delete me"u8.ToArray()),
      ],
      new FormatCreateOptions());

    image.Position = 0;
    var profile = FormatRegistry.ProbeFilesystem("RomFs", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.True,
        "A flat regular-file image emitted by the real ROMFS writer must satisfy the explicit rebuild profile.");
      Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.WholeImageRebuild));
      const FilesystemDriverCapabilities required =
        FilesystemDriverCapabilities.WriteData |
        FilesystemDriverCapabilities.Truncate |
        FilesystemDriverCapabilities.CreateFile |
        FilesystemDriverCapabilities.DeleteFile |
        FilesystemDriverCapabilities.Rename |
        FilesystemDriverCapabilities.Flush;
      Assert.That(profile.Capabilities & required, Is.EqualTo(required));
    });

    image.Position = 0;
    using (var filesystem = FormatRegistry.OpenFilesystem("RomFs", image, new(ReadOnly: false, LeaveOpen: true))) {
      const string createdName = "CREATED.BIN";
      const string renamedName = "RENAMED.BIN";
      const int truncatedLength = 733;

      var created = filesystem.CreateFile(filesystem.RootNodeId, createdName);
      using (var handle = filesystem.OpenFile(created, FileAccess.ReadWrite)) {
        handle.Write(0, MutationPayload);
        handle.SetLength(truncatedLength);
      }

      filesystem.Rename(filesystem.RootNodeId, createdName, filesystem.RootNodeId, renamedName, replace: false);
      Assert.That(filesystem.Lookup(filesystem.RootNodeId, createdName), Is.Null);
      Assert.That(filesystem.Lookup(filesystem.RootNodeId, renamedName), Is.EqualTo(created),
        "Rename must preserve the session-stable node identity.");

      filesystem.DeleteFile(filesystem.RootNodeId, "DELETE.BIN");
      filesystem.Flush();
    }

    // Re-open from the modified real ROMFS bytes rather than trusting the
    // mutable in-memory namespace. This is the durability qualification gate.
    image.Position = 0;
    var reopenedProfile = FormatRegistry.ProbeFilesystem("RomFs", image);
    Assert.That(reopenedProfile.CanMount, Is.True);
    image.Position = 0;
    using var reopened = FormatRegistry.OpenFilesystem("RomFs", image, new(ReadOnly: true, LeaveOpen: true));

    Assert.That(reopened.Lookup(reopened.RootNodeId, "CREATED.BIN"), Is.Null);
    Assert.That(reopened.Lookup(reopened.RootNodeId, "DELETE.BIN"), Is.Null);

    var keep = reopened.Lookup(reopened.RootNodeId, "KEEP.BIN");
    Assert.That(keep, Is.Not.Null);
    Assert.That(ReadAll(reopened, keep!.Value), Is.EqualTo(InitialPayload));

    var renamed = reopened.Lookup(reopened.RootNodeId, "RENAMED.BIN");
    Assert.That(renamed, Is.Not.Null);
    Assert.That(ReadAll(reopened, renamed!.Value), Is.EqualTo(MutationPayload[..733]));
  }

  [Test]
  [Category("Mounting")]
  public void WholeImagePublicationRestoresOriginalBytesWhenTargetWriteFails() {
    var original = "original-image"u8.ToArray();
    using var backing = new MemoryStream();
    backing.Write(original);
    backing.Position = 0;
    using var target = new FailOnceWriteStream(backing, failAfterBytes: 4);

    Assert.Throws<IOException>(() => WholeImageRebuildCommitter.Replace(
      target,
      candidate => candidate.Write("replacement-image"u8)));

    backing.Position = 0;
    Assert.That(backing.ToArray(), Is.EqualTo(original));
  }

  [Test]
  [Category("Mounting")]
  public void SpoolingSessionSupportsFileOffsetsAboveTwoGiBWithoutManagedArrayAllocation() {
    var profile = new FilesystemDriverProfile(
      "streamed-test", "spooled rebuild",
      FilesystemMountCapabilityResolver.CoreReadCapabilities |
      FilesystemMountCapabilityResolver.CoreWriteCapabilities,
      FilesystemMutationModel.WholeImageRebuild,
      CanMount: true, CanMountWritable: true, []);
    using var image = new MemoryStream();
    using var session = MutableRebuildFilesystemSession.OpenSpooling(
      profile, image, [],
      rebuild: static (destination, files) => {
        Assert.That(files.Count, Is.EqualTo(1));
        var staged = files[0];
        Assert.That(staged.Length, Is.EqualTo((long)int.MaxValue + 10L));
        using var source = staged.OpenRead!();
        source.Position = staged.Length - 1;
        Assert.That(source.ReadByte(), Is.EqualTo(0xA7));
        destination.WriteByte(0x17);
      });
    var node = session.CreateFile(session.RootNodeId, "huge.dat");
    const long large = (long)int.MaxValue + 10;
    using (var handle = session.OpenFile(node, FileAccess.ReadWrite)) {
      handle.SetLength(large);
      handle.Write(large - 1, [0xA7]);
      Span<byte> last = stackalloc byte[1];
      Assert.Multiple(() => {
        Assert.That(handle.Length, Is.EqualTo(large));
        Assert.That(handle.Read(large - 1, last), Is.EqualTo(1));
        Assert.That(last[0], Is.EqualTo(0xA7));
      });
    }
    session.Flush();
    Assert.That(image.ToArray(), Is.EqualTo(new byte[] { 0x17 }));
  }

  [Test]
  [Category("Mounting")]
  public void MutableSessionEnforcesAggregateContentQuotaWithoutCorruptingExistingData() {
    var capabilities =
      FilesystemDriverCapabilities.EnumerateDirectories |
      FilesystemDriverCapabilities.ReadData |
      FilesystemDriverCapabilities.RandomAccess |
      FilesystemDriverCapabilities.StableNodeIds |
      FilesystemDriverCapabilities.WriteData |
      FilesystemDriverCapabilities.Truncate |
      FilesystemDriverCapabilities.CreateFile |
      FilesystemDriverCapabilities.DeleteFile |
      FilesystemDriverCapabilities.Rename |
      FilesystemDriverCapabilities.Flush;

    var profile = new FilesystemDriverProfile(
      "budget-test",
      "bounded mutable namespace",
      capabilities,
      FilesystemMutationModel.WholeImageRebuild,
      CanMount: true,
      CanMountWritable: true,
      []);
    using var image = new MemoryStream();
    using var filesystem = new MutableRebuildFilesystemSession(
      profile,
      image,
      [RebuildFilesystemEntry.File("original.bin", "abcd"u8.ToArray())],
      rebuild: static (destination, entries) => {
        foreach (var entry in entries.Where(static item => item.Kind == FilesystemNodeKind.RegularFile))
          destination.Write(entry.Data.Span);
      },
      maxTotalContentBytes: 8);
    var original = filesystem.Lookup(filesystem.RootNodeId, "original.bin")!.Value;
    var second = filesystem.CreateFile(filesystem.RootNodeId, "second.bin");
    using var firstHandle = filesystem.OpenFile(original, FileAccess.ReadWrite);
    using var secondHandle = filesystem.OpenFile(second, FileAccess.ReadWrite);

    secondHandle.Write(100, ReadOnlySpan<byte>.Empty);
    Assert.That(secondHandle.Length, Is.Zero, "A zero-length write past EOF must not allocate data.");

    Assert.Throws<IOException>(() => secondHandle.Write(0, "12345"u8));
    Assert.Multiple(() => {
      Assert.That(firstHandle.Length, Is.EqualTo(4));
      Assert.That(secondHandle.Length, Is.Zero);
    });

    firstHandle.SetLength(2);
    secondHandle.Write(0, "12345"u8);
    Assert.Multiple(() => {
      Assert.That(firstHandle.Length, Is.EqualTo(2));
      Assert.That(secondHandle.Length, Is.EqualTo(5));
    });
    Assert.Throws<IOException>(() => secondHandle.SetLength(7));
    Assert.That(secondHandle.Length, Is.EqualTo(5));
    filesystem.Flush();
  }

  private static byte[] ReadAll(IFilesystemSession filesystem, FilesystemNodeId nodeId) {
    using var handle = filesystem.OpenFile(nodeId, FileAccess.Read);
    var result = new byte[checked((int)handle.Length)];
    var offset = 0;
    while (offset < result.Length) {
      var read = handle.Read(offset, result.AsSpan(offset));
      if (read <= 0)
        throw new EndOfStreamException();
      offset += read;
    }
    return result;
  }

  /// <summary>
  /// Seekable/readable/writable wrapper that fails exactly once after a partial
  /// publication write, then permits the commit layer's rollback writes.
  /// </summary>
  private sealed class FailOnceWriteStream(Stream inner, int failAfterBytes) : Stream {
    private bool _failed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
      => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) {
      if (!_failed && buffer.Length > failAfterBytes) {
        inner.Write(buffer[..failAfterBytes]);
        _failed = true;
        throw new IOException("Injected publication failure.");
      }
      inner.Write(buffer);
    }

    protected override void Dispose(bool disposing) {
      // The test owns the backing stream independently; disposing this wrapper
      // must not hide the bytes used to assert rollback.
      base.Dispose(disposing);
    }
  }
}
