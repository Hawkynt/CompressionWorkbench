using System.Security.AccessControl;
using Compression.Registry;
using DokanNet;
using DokanAccess = DokanNet.FileAccess;

namespace Compression.Mounting.Dokan;

/// <summary>
/// Dokan callback bridge over the mount-grade filesystem contract. Paths are
/// resolved only for namespace operations; open handles keep stable node ids so
/// rename and delete-pending do not turn data I/O back into path-based access.
/// </summary>
public sealed class DokanFilesystemOperations : IDokanOperations {
  private const NtStatus MediaWriteProtected = (NtStatus)0xC00000A2L; // STATUS_MEDIA_WRITE_PROTECTED

  private const DokanAccess DataWriteAccess =
    DokanAccess.WriteData |
    DokanAccess.AppendData |
    DokanAccess.GenericWrite |
    DokanAccess.GenericAll;

  private const DokanAccess DataReadAccess =
    DokanAccess.ReadData |
    DokanAccess.Execute |
    DokanAccess.GenericExecute |
    DokanAccess.GenericRead |
    DokanAccess.GenericAll;

  private const DokanAccess MetadataWriteAccess =
    DokanAccess.WriteExtendedAttributes |
    DokanAccess.WriteAttributes |
    DokanAccess.ChangePermissions |
    DokanAccess.SetOwnership |
    DokanAccess.DeleteChild |
    DokanAccess.GenericWrite |
    DokanAccess.GenericAll;

  private const DokanAccess DeleteAccess =
    DokanAccess.Delete |
    DokanAccess.GenericAll;

  private readonly IFilesystemSession _filesystem;
  private readonly bool _readOnly;
  private readonly object _openHandlesSync = new();
  private readonly Dictionary<FilesystemNodeId, List<DokanOpenHandle>> _openHandles = [];
  private volatile bool _mounted;
  private string? _mountedTarget;

  public DokanFilesystemOperations(IFilesystemSession filesystem, bool readOnly = true) {
    this._filesystem = filesystem ?? throw new ArgumentNullException(nameof(filesystem));
    this._readOnly = readOnly;
  }

  public bool IsMounted => this._mounted;
  public string? MountedTarget => Volatile.Read(ref this._mountedTarget);

  public NtStatus CreateFile(
    string fileName,
    DokanAccess access,
    FileShare share,
    FileMode mode,
    FileOptions options,
    FileAttributes attributes,
    IDokanFileInfo info
  ) {
    ArgumentNullException.ThrowIfNull(info);

    try {
      var exists = FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId);
      if (!exists) {
        if (mode is FileMode.Open or FileMode.Truncate)
          return DokanResult.FileNotFound;
        if (this._readOnly)
          return MediaWriteProtected;
        if (info.IsDirectory && mode == FileMode.Append)
          return DokanResult.AccessDenied;
        if (mode == FileMode.Append && !WantsWriteData(access))
          return DokanResult.AccessDenied;
        if (!FilesystemPathResolver.TryResolveParent(this._filesystem, fileName, out var parent, out var name))
          return DokanResult.PathNotFound;

        if (info.IsDirectory) {
          if (!Has(FilesystemDriverCapabilities.CreateDirectory))
            return DokanResult.NotImplemented;
          nodeId = this._filesystem.CreateDirectory(parent, name);
          return OpenNode(nodeId, FilesystemNodeKind.Directory, access, share, append: false, fileAccess: null, truncate: false, info);
        }

        if (!Has(FilesystemDriverCapabilities.CreateFile))
          return DokanResult.NotImplemented;

        var created = false;
        try {
          nodeId = this._filesystem.CreateFile(parent, name);
          created = true;
          var createdOpenStatus = OpenNode(
            nodeId,
            FilesystemNodeKind.RegularFile,
            access,
            share,
            append: mode == FileMode.Append,
            fileAccess: RequiredDataAccess(access),
            truncate: false,
            info
          );
          if (createdOpenStatus == DokanResult.Success) {
            created = false;
            return createdOpenStatus;
          }

          this._filesystem.DeleteFile(parent, name);
          created = false;
          return createdOpenStatus;
        } finally {
          if (created) {
            try {
              this._filesystem.DeleteFile(parent, name);
            } catch {
              // Preserve the original open failure. The orphan cleanup is best effort.
            }
          }
        }
      }

      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory) {
        if (!info.IsDirectory && mode is not FileMode.Open and not FileMode.OpenOrCreate)
          return DokanResult.AccessDenied;
        if (mode == FileMode.CreateNew)
          return DokanResult.AlreadyExists;
        if (mode is FileMode.Create or FileMode.Truncate or FileMode.Append)
          return DokanResult.AccessDenied;
        if (this._readOnly && RequestsMutation(access))
          return MediaWriteProtected;

        info.IsDirectory = true;
        var directoryStatus = OpenNode(nodeId, node.Kind, access, share, append: false, fileAccess: null, truncate: false, info);
        if (directoryStatus != DokanResult.Success)
          return directoryStatus;
        return mode == FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;
      }

      if (info.IsDirectory)
        return DokanResult.NotADirectory;
      if (node.Kind != FilesystemNodeKind.RegularFile)
        return DokanResult.NotImplemented;
      if (mode == FileMode.CreateNew)
        return DokanResult.AlreadyExists;

      var truncates = mode is FileMode.Create or FileMode.Truncate;
      var appends = mode == FileMode.Append;
      if ((truncates || appends) && !WantsWriteData(access))
        return DokanResult.AccessDenied;
      if (this._readOnly && (RequestsMutation(access) || truncates || appends))
        return MediaWriteProtected;
      if (truncates && !Has(FilesystemDriverCapabilities.Truncate))
        return DokanResult.NotImplemented;

      var status = OpenNode(
        nodeId,
        node.Kind,
        access,
        share,
        append: appends,
        fileAccess: RequiredDataAccess(access),
        truncate: truncates,
        info
      );
      if (status != DokanResult.Success)
        return status;

      return mode is FileMode.Create or FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public void Cleanup(string fileName, IDokanFileInfo info) {
    if (!info.DeletePending || this._readOnly || info.Context is not DokanOpenHandle handle)
      return;

    lock (this._openHandlesSync) {
      var target = handle.DeleteTarget;
      if (target is null)
        return;

      try {
        if (target.IsDirectory)
          this._filesystem.RemoveDirectory(target.Parent, target.Name);
        else
          this._filesystem.DeleteFile(target.Parent, target.Name);
      } catch {
        // Cleanup cannot report failure to Dokan. DeleteFile/DeleteDirectory have
        // already validated the request; keep teardown safe if media changed.
      }
    }
  }

  public void CloseFile(string fileName, IDokanFileInfo info) {
    if (info.Context is not DokanOpenHandle handle) {
      info.Context = null;
      return;
    }

    lock (this._openHandlesSync) {
      if (this._openHandles.TryGetValue(handle.NodeId, out var handles)) {
        handles.Remove(handle);
        if (handles.Count == 0)
          this._openHandles.Remove(handle.NodeId);
      }
    }

    try {
      handle.Dispose();
    } finally {
      info.Context = null;
    }
  }

  public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info) {
    bytesRead = 0;
    if (offset < 0)
      return DokanResult.InvalidParameter;

    try {
      if (info.Context is DokanOpenHandle opened) {
        if (!opened.CanReadData)
          return DokanResult.AccessDenied;
        if (opened.File is not { } openedFile)
          return DokanResult.InvalidHandle;
        lock (opened.SyncRoot)
          bytesRead = openedFile.Read(offset, buffer);
        return DokanResult.Success;
      }

      if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId))
        return DokanResult.FileNotFound;
      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory)
        return DokanResult.AccessDenied;
      if (node.Kind != FilesystemNodeKind.RegularFile)
        return DokanResult.NotImplemented;

      lock (this._openHandlesSync) {
        var shareStatus = CheckTransientAccess(nodeId, ShareIntent.Read);
        if (shareStatus != DokanResult.Success)
          return shareStatus;
        using var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.Read);
        bytesRead = file.Read(offset, buffer);
      }
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info) {
    bytesWritten = 0;
    if (this._readOnly)
      return MediaWriteProtected;
    if (offset < -1)
      return DokanResult.InvalidParameter;
    if (!Has(FilesystemDriverCapabilities.WriteData))
      return DokanResult.NotImplemented;

    try {
      if (info.Context is DokanOpenHandle opened) {
        if (!opened.CanWriteData)
          return DokanResult.AccessDenied;
        if (opened.File is not { } openedFile)
          return DokanResult.InvalidHandle;
        lock (opened.SyncRoot)
          return WriteToHandle(openedFile, opened.Append, buffer, out bytesWritten, offset, info);
      }

      if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId))
        return DokanResult.FileNotFound;
      if (this._filesystem.Stat(nodeId).Kind != FilesystemNodeKind.RegularFile)
        return DokanResult.AccessDenied;

      lock (this._openHandlesSync) {
        var shareStatus = CheckTransientAccess(nodeId, ShareIntent.Write);
        if (shareStatus != DokanResult.Success)
          return shareStatus;
        using var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.ReadWrite);
        return WriteToHandle(file, append: false, buffer, out bytesWritten, offset, info);
      }
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) {
    try {
      if (!Has(FilesystemDriverCapabilities.Flush))
        return DokanResult.Success;
      if (info.Context is DokanOpenHandle { File: { } file } opened)
        lock (opened.SyncRoot)
          file.Flush();
      this._filesystem.Flush();
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info) {
    fileInfo = default;
    try {
      FilesystemNodeId nodeId;
      if (info.Context is DokanOpenHandle opened)
        nodeId = opened.NodeId;
      else if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out nodeId))
        return DokanResult.FileNotFound;
      fileInfo = ToDokanFileInformation(fileName, this._filesystem.Stat(nodeId));
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info) {
    files = [];
    try {
      FilesystemNodeId directoryId;
      if (info.Context is DokanOpenHandle opened)
        directoryId = opened.NodeId;
      else if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out directoryId))
        return DokanResult.PathNotFound;
      if (this._filesystem.Stat(directoryId).Kind != FilesystemNodeKind.Directory)
        return DokanResult.NotADirectory;

      var entries = this._filesystem.Enumerate(directoryId);
      var result = new List<FileInformation>(entries.Count);
      foreach (var entry in entries)
        result.Add(ToDokanFileInformation(entry.Name, this._filesystem.Stat(entry.NodeId)));
      files = result;
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info) {
    files = [];
    return DokanResult.NotImplemented;
  }

  public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) {
    if (attributes == 0)
      return DokanResult.Success;
    return this._readOnly ? MediaWriteProtected : DokanResult.NotImplemented;
  }

  public NtStatus SetFileTime(
    string fileName,
    DateTime? creationTime,
    DateTime? lastAccessTime,
    DateTime? lastWriteTime,
    IDokanFileInfo info
  ) {
    if (this._readOnly)
      return MediaWriteProtected;
    if (!Has(FilesystemDriverCapabilities.SetMetadata))
      return DokanResult.NotImplemented;

    try {
      FilesystemNodeId nodeId;
      if (info.Context is DokanOpenHandle opened) {
        if (!opened.CanWriteMetadata)
          return DokanResult.AccessDenied;
        nodeId = opened.NodeId;
      } else if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out nodeId)) {
        return DokanResult.FileNotFound;
      }

      this._filesystem.SetMetadata(nodeId, new(
        Created: ToOffset(creationTime),
        Modified: ToOffset(lastWriteTime),
        Accessed: ToOffset(lastAccessTime)
      ));
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    => ValidateDelete(fileName, info, isDirectory: false);

  public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    => ValidateDelete(fileName, info, isDirectory: true);

  public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info) {
    if (this._readOnly)
      return MediaWriteProtected;
    if (!Has(FilesystemDriverCapabilities.Rename))
      return DokanResult.NotImplemented;

    try {
      if (!FilesystemPathResolver.TryResolveParent(this._filesystem, oldName, out var oldParent, out var oldLeaf))
        return DokanResult.PathNotFound;
      if (!FilesystemPathResolver.TryResolveParent(this._filesystem, newName, out var newParent, out var newLeaf))
        return DokanResult.PathNotFound;

      var sourceNodeId = this._filesystem.Lookup(oldParent, oldLeaf);
      if (sourceNodeId is null)
        return DokanResult.FileNotFound;

      if (info.Context is DokanOpenHandle opened) {
        if (opened.NodeId != sourceNodeId.Value)
          return DokanResult.InvalidHandle;
        if (!opened.CanDelete)
          return DokanResult.AccessDenied;
      }

      var destinationNodeId = this._filesystem.Lookup(newParent, newLeaf);
      var samePath = oldParent == newParent && NamesEqual(oldLeaf, newLeaf);
      if (!samePath && destinationNodeId is not null && !replace)
        return DokanResult.AlreadyExists;

      lock (this._openHandlesSync) {
        var sourceShareStatus = CheckDeleteSharing(sourceNodeId.Value, info.Context as DokanOpenHandle);
        if (sourceShareStatus != DokanResult.Success)
          return sourceShareStatus;

        if (!samePath && destinationNodeId is { } destination && destination != sourceNodeId.Value) {
          if (HasDeletePending(destination))
            return NtStatus.DeletePending;
          var destinationShareStatus = CheckDeleteSharing(destination, requester: null);
          if (destinationShareStatus != DokanResult.Success)
            return destinationShareStatus;
        }

        this._filesystem.Rename(oldParent, oldLeaf, newParent, newLeaf, replace);
        UpdateDeleteTargets(sourceNodeId.Value, oldParent, oldLeaf, newParent, newLeaf);
      }
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    => SetLength(fileName, length, allocationOnly: false, info);

  public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    => SetLength(fileName, length, allocationOnly: true, info);

  // Dokany performs byte-range locking itself unless DOKAN_OPTION_FILELOCK_USER_MODE
  // is enabled. The backend intentionally leaves that option disabled.
  public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    => DokanResult.NotImplemented;

  public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    => DokanResult.NotImplemented;

  public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info) {
    freeBytesAvailable = 0;
    totalNumberOfBytes = 0;
    totalNumberOfFreeBytes = 0;
    return DokanResult.NotImplemented;
  }

  public NtStatus GetVolumeInformation(
    out string volumeLabel,
    out FileSystemFeatures features,
    out string fileSystemName,
    out uint maximumComponentLength,
    IDokanFileInfo info
  ) {
    volumeLabel = this._filesystem.Profile.FormatId;
    fileSystemName = "CWBFS";
    maximumComponentLength = 255;
    features = this._readOnly ? FileSystemFeatures.ReadOnlyVolume : 0;

    var capabilities = this._filesystem.Profile.Capabilities;
    if (capabilities.HasFlag(FilesystemDriverCapabilities.CaseSensitiveNames))
      features |= FileSystemFeatures.CaseSensitiveSearch;
    if (capabilities.HasFlag(FilesystemDriverCapabilities.CasePreservingNames))
      features |= FileSystemFeatures.CasePreservedNames;
    return DokanResult.Success;
  }

  public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info) {
    security = null;
    return DokanResult.NotImplemented;
  }

  public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    => this._readOnly ? MediaWriteProtected : DokanResult.NotImplemented;

  public NtStatus Mounted(string mountPoint, IDokanFileInfo info) {
    Volatile.Write(ref this._mountedTarget, mountPoint);
    this._mounted = true;
    return DokanResult.Success;
  }

  public NtStatus Unmounted(IDokanFileInfo info) {
    this._mounted = false;
    return DokanResult.Success;
  }

  public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info) {
    streams = [];
    return DokanResult.NotImplemented;
  }

  private NtStatus OpenNode(
    FilesystemNodeId nodeId,
    FilesystemNodeKind kind,
    DokanAccess access,
    FileShare share,
    bool append,
    System.IO.FileAccess? fileAccess,
    bool truncate,
    IDokanFileInfo info
  ) {
    lock (this._openHandlesSync) {
      var shareStatus = CheckOpenSharing(nodeId, access, share);
      if (shareStatus != DokanResult.Success)
        return shareStatus;

      IFilesystemFileHandle? file = null;
      try {
        if (fileAccess is { } requestedFileAccess) {
          file = this._filesystem.OpenFile(nodeId, requestedFileAccess);
          if (truncate)
            file.SetLength(0);
        }

        var handle = new DokanOpenHandle(nodeId, kind, file, access, share, append);
        if (!this._openHandles.TryGetValue(nodeId, out var handles)) {
          handles = [];
          this._openHandles.Add(nodeId, handles);
        }
        handles.Add(handle);
        info.Context = handle;
        file = null;
        return DokanResult.Success;
      } finally {
        file?.Dispose();
      }
    }
  }

  private NtStatus CheckOpenSharing(FilesystemNodeId nodeId, DokanAccess access, FileShare share) {
    if (!this._openHandles.TryGetValue(nodeId, out var handles))
      return DokanResult.Success;
    if (handles.Any(static handle => handle.DeleteTarget is not null))
      return NtStatus.DeletePending;

    var requestedIntent = ToShareIntent(access);
    foreach (var existing in handles)
      if (!Allows(existing.Share, requestedIntent) || !Allows(share, existing.Intent))
        return NtStatus.SharingViolation;
    return DokanResult.Success;
  }

  private NtStatus CheckTransientAccess(FilesystemNodeId nodeId, ShareIntent requestedIntent) {
    if (!this._openHandles.TryGetValue(nodeId, out var handles))
      return DokanResult.Success;
    if (handles.Any(static handle => handle.DeleteTarget is not null))
      return NtStatus.DeletePending;
    return handles.All(handle => Allows(handle.Share, requestedIntent))
      ? DokanResult.Success
      : NtStatus.SharingViolation;
  }

  private NtStatus CheckDeleteSharing(FilesystemNodeId nodeId, DokanOpenHandle? requester) {
    if (!this._openHandles.TryGetValue(nodeId, out var handles))
      return DokanResult.Success;
    foreach (var handle in handles)
      if (!ReferenceEquals(handle, requester) && !handle.Share.HasFlag(FileShare.Delete))
        return NtStatus.SharingViolation;
    return DokanResult.Success;
  }

  private bool HasDeletePending(FilesystemNodeId nodeId)
    => this._openHandles.TryGetValue(nodeId, out var handles) &&
       handles.Any(static handle => handle.DeleteTarget is not null);

  private NtStatus ValidateDelete(string fileName, IDokanFileInfo info, bool isDirectory) {
    if (this._readOnly)
      return MediaWriteProtected;
    var capability = isDirectory ? FilesystemDriverCapabilities.RemoveDirectory : FilesystemDriverCapabilities.DeleteFile;
    if (!Has(capability))
      return DokanResult.NotImplemented;

    try {
      if (!FilesystemPathResolver.TryResolveParent(this._filesystem, fileName, out var parent, out var name))
        return DokanResult.PathNotFound;
      var nodeId = this._filesystem.Lookup(parent, name);
      if (nodeId is null)
        return DokanResult.FileNotFound;
      var node = this._filesystem.Stat(nodeId.Value);
      if (isDirectory != (node.Kind == FilesystemNodeKind.Directory))
        return isDirectory ? DokanResult.NotADirectory : DokanResult.AccessDenied;

      if (info.Context is not DokanOpenHandle handle || handle.NodeId != nodeId.Value)
        return DokanResult.InvalidHandle;
      if (!handle.CanDelete)
        return DokanResult.AccessDenied;

      if (!info.DeletePending) {
        lock (this._openHandlesSync)
          SetDeleteTarget(nodeId.Value, target: null);
        return DokanResult.Success;
      }

      if (isDirectory && this._filesystem.Enumerate(nodeId.Value).Count != 0)
        return NtStatus.DirectoryNotEmpty;

      // The neutral contract has no separate "can delete this node" preflight.
      // Opening a regular file for write preserves drivers' existing locked-file
      // validation before Cleanup reaches the non-reporting delete boundary.
      if (!isDirectory) {
        using var writable = this._filesystem.OpenFile(nodeId.Value, System.IO.FileAccess.Write);
      }

      lock (this._openHandlesSync) {
        var shareStatus = CheckDeleteSharing(nodeId.Value, handle);
        if (shareStatus != DokanResult.Success)
          return shareStatus;
        SetDeleteTarget(nodeId.Value, new(parent, name, isDirectory));
      }
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  private NtStatus SetLength(string fileName, long length, bool allocationOnly, IDokanFileInfo info) {
    if (this._readOnly)
      return MediaWriteProtected;
    if (length < 0)
      return DokanResult.InvalidParameter;
    if (!Has(FilesystemDriverCapabilities.Truncate))
      return DokanResult.NotImplemented;

    try {
      if (info.Context is DokanOpenHandle opened) {
        if (!opened.CanWriteData)
          return DokanResult.AccessDenied;
        if (opened.File is not { } openedFile)
          return DokanResult.InvalidHandle;
        lock (opened.SyncRoot)
          if (!allocationOnly || length < openedFile.Length)
            openedFile.SetLength(length);
        return DokanResult.Success;
      }

      if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId))
        return DokanResult.FileNotFound;
      if (this._filesystem.Stat(nodeId).Kind != FilesystemNodeKind.RegularFile)
        return DokanResult.AccessDenied;

      lock (this._openHandlesSync) {
        var shareStatus = CheckTransientAccess(nodeId, ShareIntent.Write);
        if (shareStatus != DokanResult.Success)
          return shareStatus;
        using var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.ReadWrite);
        if (!allocationOnly || length < file.Length)
          file.SetLength(length);
      }
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  private static NtStatus WriteToHandle(
    IFilesystemFileHandle file,
    bool append,
    byte[] buffer,
    out int bytesWritten,
    long offset,
    IDokanFileInfo info
  ) {
    bytesWritten = 0;

    if (info.PagingIo) {
      if (offset < 0)
        return DokanResult.InvalidParameter;
      if (offset >= file.Length)
        return DokanResult.Success;

      var available = file.Length - offset;
      var count = (int)Math.Min(buffer.Length, available);
      if (count == 0)
        return DokanResult.Success;
      file.Write(offset, buffer.AsSpan(0, count));
      bytesWritten = count;
      return DokanResult.Success;
    }

    var writeOffset = offset == -1 || info.WriteToEndOfFile || append ? file.Length : offset;
    file.Write(writeOffset, buffer);
    bytesWritten = buffer.Length;
    return DokanResult.Success;
  }

  private void SetDeleteTarget(FilesystemNodeId nodeId, DokanDeleteTarget? target) {
    if (!this._openHandles.TryGetValue(nodeId, out var handles))
      return;

    foreach (var openHandle in handles)
      openHandle.DeleteTarget = target;
  }

  private void UpdateDeleteTargets(
    FilesystemNodeId nodeId,
    FilesystemNodeId oldParent,
    string oldName,
    FilesystemNodeId newParent,
    string newName
  ) {
    if (!this._openHandles.TryGetValue(nodeId, out var handles))
      return;

    foreach (var handle in handles)
      if (handle.DeleteTarget is { } target &&
          target.Parent == oldParent &&
          NamesEqual(target.Name, oldName))
        handle.DeleteTarget = new(newParent, newName, target.IsDirectory);
  }

  private bool NamesEqual(string left, string right)
    => string.Equals(
      left,
      right,
      Has(FilesystemDriverCapabilities.CaseSensitiveNames) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
    );

  private bool Has(FilesystemDriverCapabilities capability)
    => this._filesystem.Profile.Capabilities.HasFlag(capability);

  private static bool RequestsMutation(DokanAccess access)
    => WantsWriteData(access) ||
       (access & (MetadataWriteAccess | DeleteAccess)) != DokanAccess.None;

  private static bool WantsReadData(DokanAccess access)
    => (access & DataReadAccess) != DokanAccess.None;

  private static bool WantsWriteData(DokanAccess access)
    => (access & DataWriteAccess) != DokanAccess.None;

  private static System.IO.FileAccess? RequiredDataAccess(DokanAccess access)
    => (WantsReadData(access), WantsWriteData(access)) switch {
      (true, true) => System.IO.FileAccess.ReadWrite,
      (_, true) => System.IO.FileAccess.Write,
      (true, _) => System.IO.FileAccess.Read,
      _ => null,
    };

  private static ShareIntent ToShareIntent(DokanAccess access) {
    var result = ShareIntent.None;
    if (WantsReadData(access))
      result |= ShareIntent.Read;
    if (WantsWriteData(access))
      result |= ShareIntent.Write;
    if ((access & DeleteAccess) != DokanAccess.None)
      result |= ShareIntent.Delete;
    return result;
  }

  private static bool Allows(FileShare share, ShareIntent intent)
    => (!intent.HasFlag(ShareIntent.Read) || share.HasFlag(FileShare.Read)) &&
       (!intent.HasFlag(ShareIntent.Write) || share.HasFlag(FileShare.Write)) &&
       (!intent.HasFlag(ShareIntent.Delete) || share.HasFlag(FileShare.Delete));

  private FileInformation ToDokanFileInformation(string name, FilesystemNodeInfo node)
    => new() {
      FileName = Path.GetFileName(name.TrimEnd('\\', '/')),
      Attributes = ToFileAttributes(node.Kind),
      CreationTime = ToDateTime(node.Created),
      LastAccessTime = ToDateTime(node.Accessed),
      LastWriteTime = ToDateTime(node.Modified),
      Length = node.Kind == FilesystemNodeKind.RegularFile ? node.Size : 0,
    };

  private FileAttributes ToFileAttributes(FilesystemNodeKind kind) {
    var attributes = kind switch {
      FilesystemNodeKind.Directory => FileAttributes.Directory,
      FilesystemNodeKind.SymbolicLink => FileAttributes.ReparsePoint,
      _ => FileAttributes.Normal,
    };
    return this._readOnly ? attributes | FileAttributes.ReadOnly : attributes;
  }

  private static DateTime? ToDateTime(DateTimeOffset? value) => value?.UtcDateTime;
  private static DateTimeOffset? ToOffset(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

  private static NtStatus MapException(Exception exception)
    => exception switch {
      FileNotFoundException or KeyNotFoundException => DokanResult.FileNotFound,
      DirectoryNotFoundException => DokanResult.PathNotFound,
      UnauthorizedAccessException => DokanResult.AccessDenied,
      ObjectDisposedException => DokanResult.InvalidHandle,
      ArgumentException or ArgumentOutOfRangeException => DokanResult.InvalidParameter,
      NotSupportedException => DokanResult.NotImplemented,
      IOException => DokanResult.Unsuccessful,
      _ => DokanResult.InternalError,
    };

  [Flags]
  private enum ShareIntent : byte {
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Delete = 1 << 2,
  }

  private sealed class DokanOpenHandle(
    FilesystemNodeId nodeId,
    FilesystemNodeKind kind,
    IFilesystemFileHandle? file,
    DokanAccess access,
    FileShare share,
    bool append
  ) : IDisposable {
    private IFilesystemFileHandle? _file = file;

    public object SyncRoot { get; } = new();
    public FilesystemNodeId NodeId { get; } = nodeId;
    public FilesystemNodeKind Kind { get; } = kind;
    public DokanAccess Access { get; } = access;
    public FileShare Share { get; } = share;
    public ShareIntent Intent { get; } = ToShareIntent(access);
    public bool Append { get; } = append;
    public bool CanReadData => WantsReadData(this.Access);
    public bool CanWriteData => WantsWriteData(this.Access);
    public bool CanWriteMetadata => (this.Access & MetadataWriteAccess) != DokanAccess.None;
    public bool CanDelete => (this.Access & DeleteAccess) != DokanAccess.None;
    public IFilesystemFileHandle? File => Volatile.Read(ref this._file);
    public DokanDeleteTarget? DeleteTarget { get; set; }

    public void Dispose() => Interlocked.Exchange(ref this._file, null)?.Dispose();
  }

  private sealed record DokanDeleteTarget(FilesystemNodeId Parent, string Name, bool IsDirectory);
}
