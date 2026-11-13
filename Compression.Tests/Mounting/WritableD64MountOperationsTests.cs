using System.Security.Principal;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using Compression.Registry;
using DokanNet;
using FileSystem.D64;
using DokanAccess = DokanNet.FileAccess;
using HostAccess = System.IO.FileAccess;

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
      Assert.That(operations.WriteFile(handleId, long.MaxValue, "oversize"u8, out _), Is.EqualTo(FuseErrno.InvalidArgument));
      Assert.That(operations.OpenFile(created.Inode, 0x200, out var invalidTruncateHandle), Is.EqualTo(FuseErrno.InvalidArgument));
      Assert.That(invalidTruncateHandle, Is.Zero);
      Assert.That(operations.WriteFile(handleId, 0, "0123456789"u8, out var written), Is.Zero);
      Assert.That(written, Is.EqualTo(10));
      Assert.That(operations.SetAttributes(created.Inode, 1 << 3, 12, handleId, out var resized), Is.Zero);
      Assert.That(resized.Node.Size, Is.EqualTo(12));
      Assert.That(operations.ReleaseFile(handleId), Is.Zero);

      Assert.That(
        operations.Rename(FuseFilesystemOperations.RootInode, "NEWFILE", FuseFilesystemOperations.RootInode, "ORIGINAL", flags: 1),
        Is.EqualTo(FuseErrno.Exists)
      );
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
          DokanAccess.GenericRead | DokanAccess.GenericWrite | DokanAccess.Delete,
          FileShare.ReadWrite | FileShare.Delete,
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


  [Test]
  public void DokanEnforcesBidirectionalShareModesAndPerHandleDataAccess() {
    using var image = CreateImage();
    var descriptor = new D64FormatDescriptor();

    using var filesystem = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
    var operations = new DokanFilesystemOperations(filesystem, readOnly: false);

    var first = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead,
        FileShare.Read,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        first
      ),
      Is.EqualTo(DokanResult.Success)
    );

    var wantsWrite = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericWrite,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        wantsWrite
      ),
      Is.EqualTo(NtStatus.SharingViolation),
      "the existing handle did not share write access"
    );
    operations.CloseFile("\\ORIGINAL", first);

    var permissive = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        permissive
      ),
      Is.EqualTo(DokanResult.Success)
    );

    var exclusive = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead,
        FileShare.None,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        exclusive
      ),
      Is.EqualTo(NtStatus.SharingViolation),
      "the new handle must also share access already granted to existing handles"
    );
    operations.CloseFile("\\ORIGINAL", permissive);

    var metadataOnly = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.ReadAttributes,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        metadataOnly
      ),
      Is.EqualTo(DokanResult.Success)
    );

    var readBuffer = new byte[1];
    Assert.Multiple(() => {
      Assert.That(
        operations.ReadFile("\\ORIGINAL", readBuffer, out _, 0, metadataOnly),
        Is.EqualTo(DokanResult.AccessDenied),
        "a metadata-only context must not fall back to a path-based read"
      );
      Assert.That(
        operations.WriteFile("\\ORIGINAL", "x"u8.ToArray(), out _, 0, metadataOnly),
        Is.EqualTo(DokanResult.AccessDenied),
        "a metadata-only context must not fall back to a path-based write"
      );
    });
    operations.CloseFile("\\ORIGINAL", metadataOnly);
  }

  [Test]
  public void DokanDeletePendingBlocksReopenCanBeCancelledAndDeletesOnCleanup() {
    using var image = CreateImage();
    var descriptor = new D64FormatDescriptor();

    using var filesystem = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
    var operations = new DokanFilesystemOperations(filesystem, readOnly: false);
    var deleteInfo = new TestDokanFileInfo { DeletePending = true };
    var lastHandle = new TestDokanFileInfo();

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
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        lastHandle
      ),
      Is.EqualTo(DokanResult.Success)
    );
    Assert.That(operations.DeleteFile("\\ORIGINAL", deleteInfo), Is.EqualTo(DokanResult.Success));

    var blocked = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        blocked
      ),
      Is.EqualTo(NtStatus.DeletePending)
    );

    deleteInfo.DeletePending = false;
    Assert.That(
      operations.DeleteFile("\\ORIGINAL", deleteInfo),
      Is.EqualTo(DokanResult.Success),
      "Dokan can notify that a previously requested delete was cancelled"
    );

    var reopened = new TestDokanFileInfo();
    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        reopened
      ),
      Is.EqualTo(DokanResult.Success)
    );
    operations.CloseFile("\\ORIGINAL", reopened);

    deleteInfo.DeletePending = true;
    Assert.That(operations.DeleteFile("\\ORIGINAL", deleteInfo), Is.EqualTo(DokanResult.Success));

    deleteInfo.DeletePending = false;
    operations.Cleanup("\\ORIGINAL", deleteInfo);
    operations.CloseFile("\\ORIGINAL", deleteInfo);
    Assert.That(
      filesystem.Lookup(filesystem.RootNodeId, "ORIGINAL"),
      Is.Not.Null,
      "closing the handle that requested deletion must not unlink while another handle remains"
    );

    lastHandle.DeletePending = true;
    operations.Cleanup("\\ORIGINAL", lastHandle);
    Assert.That(
      filesystem.Lookup(filesystem.RootNodeId, "ORIGINAL"),
      Is.Null,
      "delete state must follow the node so the final open handle can perform Cleanup"
    );
    operations.CloseFile("\\ORIGINAL", lastHandle);
  }

  [Test]
  public void DokanAppendPagingAndAllocationSizePreserveLogicalEndOfFile() {
    using var image = CreateImage();
    var descriptor = new D64FormatDescriptor();

    using var filesystem = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
    var operations = new DokanFilesystemOperations(filesystem, readOnly: false);
    var info = new TestDokanFileInfo();

    Assert.That(
      operations.CreateFile(
        "\\ORIGINAL",
        DokanAccess.GenericRead | DokanAccess.GenericWrite,
        FileShare.ReadWrite | FileShare.Delete,
        FileMode.Open,
        FileOptions.None,
        FileAttributes.Normal,
        info
      ),
      Is.EqualTo(DokanResult.Success)
    );

    Assert.That(
      operations.WriteFile("\\ORIGINAL", "!"u8.ToArray(), out var appended, -1, info),
      Is.EqualTo(DokanResult.Success)
    );
    Assert.That(appended, Is.EqualTo(1));
    Assert.That(operations.GetFileInformation("\\ORIGINAL", out var afterAppend, info), Is.EqualTo(DokanResult.Success));
    Assert.That(afterAppend.Length, Is.EqualTo(7));

    Assert.That(operations.SetAllocationSize("\\ORIGINAL", 4096, info), Is.EqualTo(DokanResult.Success));
    Assert.That(operations.GetFileInformation("\\ORIGINAL", out var afterAllocation, info), Is.EqualTo(DokanResult.Success));
    Assert.That(
      afterAllocation.Length,
      Is.EqualTo(7),
      "allocation growth is not the same operation as extending logical EOF"
    );

    info.PagingIo = true;
    Assert.That(
      operations.WriteFile("\\ORIGINAL", "XYZ"u8.ToArray(), out var pagingWritten, 6, info),
      Is.EqualTo(DokanResult.Success)
    );
    Assert.That(pagingWritten, Is.EqualTo(1), "paging I/O must be clipped at the current EOF");

    Assert.That(
      operations.WriteFile("\\ORIGINAL", "tail"u8.ToArray(), out var beyondEofWritten, 7, info),
      Is.EqualTo(DokanResult.Success)
    );
    Assert.That(beyondEofWritten, Is.Zero);

    info.PagingIo = false;
    var contents = new byte[7];
    Assert.That(operations.ReadFile("\\ORIGINAL", contents, out var read, 0, info), Is.EqualTo(DokanResult.Success));
    Assert.Multiple(() => {
      Assert.That(read, Is.EqualTo(7));
      Assert.That(contents, Is.EqualTo("beforeX"u8.ToArray()));
    });

    Assert.That(operations.SetAllocationSize("\\ORIGINAL", 3, info), Is.EqualTo(DokanResult.Success));
    Assert.That(operations.GetFileInformation("\\ORIGINAL", out var afterShrink, info), Is.EqualTo(DokanResult.Success));
    Assert.That(afterShrink.Length, Is.EqualTo(3));

    operations.CloseFile("\\ORIGINAL", info);
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
    using var file = reopened.OpenFile(renamedId, HostAccess.Read);
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
    public bool PagingIo { get; set; }
    public int ProcessId => 0;
    public bool SynchronousIo => true;
    public bool WriteToEndOfFile { get; init; }
    public WindowsIdentity GetRequestor() => null!;
    public bool TryResetTimeout(int milliseconds) => true;
  }
}
