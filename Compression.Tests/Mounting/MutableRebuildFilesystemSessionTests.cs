#pragma warning disable CS1591
using Compression.Lib;
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
