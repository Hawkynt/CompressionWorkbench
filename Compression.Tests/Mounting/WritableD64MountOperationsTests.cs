using System.Security.Principal;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using DokanNet;
using FileSystem.D64;
using DokanAccess = DokanNet.FileAccess;

namespace Compression.Tests.Mounting;

/// <summary>
/// D64 is the first real writable filesystem driven through both host adapter
/// operation surfaces. These tests deliberately reopen the image afterwards so
/// an adapter that only mutates an in-memory facade cannot pass.
/// </summary>
[TestFixture]
public sealed class WritableD64MountOperationsTests {
  [Test]
  public void FuseFileMutationRoundTripPersistsToD64Image() {
    using var image = CreateImage();
    var descriptor = new D64FormatDescriptor();

    using (var filesystem = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var backend = new FuseFilesystemMountBackend(new FuseRuntimeStatus(true, "libfuse3.so.3", "/usr/bin/fusermount3", null));
      var plan = FilesystemMountCapabilityResolver.Resolve(filesystem.Profile, backend.GetProfile(), MountAccessMode.ReadWrite, sourceCanWrite: true);
      Assert.That(plan.IsSupported, Is.True, string.Join("; ", plan.Reasons.Select(static reason => reason.Message)));

      using var operations = new FuseFilesystemOperations(filesystem, readOnly: false);
      Assert.That(operations.CreateDirectory(FuseFilesystemOperations.RootInode, "DIR", out _), Is.EqualTo(FuseErrno.NotSupported));

      Assert.That(
        operations.CreateFile(FuseFilesystemOperations.RootInode, "NEWFILE", flags: 0x2, out var created, out var handleId),
        Is.Zero
      );
      Assert.That(operations.WriteFile(handleId, 0, "0123456789"u8, out var written), Is.Zero);
      Assert.That(written, Is.EqualTo(10));
      Assert.That(operations.SetAttributes(created.Inode, 1 << 3, 12, handleId, out var resized), Is.Zero);
      Assert.That(resized.Node.Size, Is.EqualTo(12));
      Assert.That(operations.ReleaseFile(handleId), Is.Zero);

      Assert.That(
        operations.Rename(FuseFilesystemOperations.RootInode, "NEWFILE", FuseFilesystemOperations.RootInode, "RENAMED", flags: 0),
        Is.Zero
      );
      Assert.That(operations.Lookup(FuseFilesystemOperations.RootInode, "RENAMED", out var renamed), Is.Zero);
      Assert.That(renamed.Inode, Is.EqualTo(created.Inode), "rename must preserve the kernel-visible inode");

      Assert.That(operations.Lookup(FuseFilesystemOperations.RootInode, "ORIGINAL", out var original), Is.Zero);
      Assert.That(operations.OpenFile(original.Inode, flags: 0x2, out var detachedHandle), Is.Zero);
      Assert.That(operations.Unlink(FuseFilesystemOperations.RootInode, "ORIGINAL"), Is.Zero);
      Assert.That(operations.WriteFile(detachedHandle, 0, "detached"u8, out _), Is.Zero,
        "an unlinked but still-open node must remain a valid handle");
      Assert.That(operations.ReleaseFile(detachedHandle), Is.Zero);
      Assert.That(operations.FlushFilesystem(), Is.Zero);
    }

    AssertPersistedResult(descriptor, image);
  }

  [Test]
  public void DokanFileMutationRoundTripPersistsToD64Image() {
    using var image = CreateImage();
    var descriptor = new D64FormatDescriptor();

    using (var filesystem = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var backend = new DokanFilesystemMountBackend(new DokanRuntimeStatus(true, 210, 210, "dokan2.dll", null));
      var plan = FilesystemMountCapabilityResolver.Resolve(filesystem.Profile, backend.GetProfile(), MountAccessMode.ReadWrite, sourceCanWrite: true);
      Assert.That(plan.IsSupported, Is.True, string.Join("; ", plan.Reasons.Select(static reason => reason.Message)));

      var operations = new DokanFilesystemOperations(filesystem, readOnly: false);
      var createdInfo = new TestDokanFileInfo();
      Assert.That(
        operations.CreateFile(
          "\\NEWFILE",
          DokanAccess.GenericRead | DokanAccess.GenericWrite,
          FileShare.ReadWrite,
          FileMode.CreateNew,
          FileOptions.None,
          FileAttributes.Normal,
          createdInfo
        ),
        Is.EqualTo(DokanResult.Success)
      );
      Assert.That(operations.WriteFile("\\NEWFILE", "0123456789"u8.ToArray(), out var written, 0, createdInfo), Is.EqualTo(DokanResult.Success));
      Assert.That(written, Is.EqualTo(10));
      Assert.That(operations.SetEndOfFile("\\NEWFILE", 12, createdInfo), Is.EqualTo(DokanResult.Success));
      Assert.That(operations.MoveFile("\\NEWFILE", "\\RENAMED", replace: false, createdInfo), Is.EqualTo(DokanResult.Success));

      var deleteInfo = new TestDokanFileInfo { DeletePending = true };
      Assert.That(
        operations.CreateFile(
          "\\ORIGINAL",
          DokanAccess.GenericRead | DokanAccess.GenericWrite | DokanAccess.Delete,
          FileShare.ReadWrite | FileShare.Delete,
          FileMode.Open,
          FileOptions.None,
          FileAttributes.Normal,
          deleteInfo
        ),
        Is.EqualTo(DokanResult.Success)
      );
      Assert.That(operations.DeleteFile("\\ORIGINAL", deleteInfo), Is.EqualTo(DokanResult.Success));
      Assert.That(filesystem.Lookup(filesystem.RootNodeId, "ORIGINAL"), Is.Not.Null,
        "Dokan delete validation must not unlink before Cleanup");
      operations.Cleanup("\\ORIGINAL", deleteInfo);
      Assert.That(filesystem.Lookup(filesystem.RootNodeId, "ORIGINAL"), Is.Null);
      operations.CloseFile("\\ORIGINAL", deleteInfo);

      operations.CloseFile("\\RENAMED", createdInfo);
      Assert.That(operations.FlushFileBuffers("\\RENAMED", new TestDokanFileInfo()), Is.EqualTo(DokanResult.Success));
    }

    AssertPersistedResult(descriptor, image);
  }

  private static MemoryStream CreateImage() {
    var writer = new D64Writer();
    writer.AddFile("ORIGINAL", "before"u8.ToArray());
    return new MemoryStream(writer.Build("MOUNT-RW", "42"), writable: true);
  }

  private static void AssertPersistedResult(D64FormatDescriptor descriptor, MemoryStream image) {
    image.Position = 0;
    using var reopened = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.Multiple(() => {
      Assert.That(reopened.Lookup(reopened.RootNodeId, "ORIGINAL"), Is.Null);
      Assert.That(reopened.Lookup(reopened.RootNodeId, "RENAMED"), Is.Not.Null);
    });

    var renamedId = reopened.Lookup(reopened.RootNodeId, "RENAMED")!.Value;
    using var file = reopened.OpenFile(renamedId, FileAccess.Read);
    var data = new byte[12];
    Assert.That(file.Read(0, data), Is.EqualTo(data.Length));
    Assert.That(data, Is.EqualTo(new byte[] {
      (byte)'0', (byte)'1', (byte)'2', (byte)'3', (byte)'4',
      (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9', 0, 0,
    }));
  }

  private sealed class TestDokanFileInfo : IDokanFileInfo {
    public object? Context { get; set; }
    public bool DeletePending { get; set; }
    public bool IsDirectory { get; set; }
    public bool NoCache => false;
    public bool PagingIo => false;
    public int ProcessId => 0;
    public bool SynchronousIo => true;
    public bool WriteToEndOfFile { get; init; }
    public WindowsIdentity GetRequestor() => null!;
    public bool TryResetTimeout(int milliseconds) => true;
  }
}
