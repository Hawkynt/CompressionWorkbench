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
    DokanAccess.GenericRead |
    DokanAccess.GenericAll;

  private readonly IFilesystemSession _filesystem;
  private readonly bool _readOnly;
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
        if (!FilesystemPathResolver.TryResolveParent(this._filesystem, fileName, out var parent, out var name))
          return DokanResult.PathNotFound;

        if (info.IsDirectory) {
          if (!Has(FilesystemDriverCapabilities.CreateDirectory))
            return DokanResult.NotImplemented;
          nodeId = this._filesystem.CreateDirectory(parent, name);
          info.Context = new DokanOpenHandle(nodeId, FilesystemNodeKind.Directory, null, append: false);
          return DokanResult.Success;
        }

        if (!Has(FilesystemDriverCapabilities.CreateFile))
          return DokanResult.NotImplemented;
        nodeId = this._filesystem.CreateFile(parent, name);
        var createdFile = this._filesystem.OpenFile(nodeId, RequiredFileAccess(access, mode));
        info.Context = new DokanOpenHandle(nodeId, FilesystemNodeKind.RegularFile, createdFile, mode == FileMode.Append);
        return DokanResult.Success;
      }

      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory) {
        if (!info.IsDirectory && mode is not FileMode.Open and not FileMode.OpenOrCreate)
          return DokanResult.AccessDenied;
        if (mode == FileMode.CreateNew)
          return DokanResult.AlreadyExists;
        info.IsDirectory = true;
        info.Context = new DokanOpenHandle(nodeId, node.Kind, null, append: false);
        return mode == FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;
      }

      if (info.IsDirectory)
        return DokanResult.NotADirectory;
      if (node.Kind != FilesystemNodeKind.RegularFile)
        return DokanResult.NotImplemented;
      if (mode == FileMode.CreateNew)
        return DokanResult.AlreadyExists;

      var fileAccess = RequiredFileAccess(access, mode);
      if (fileAccess != System.IO.FileAccess.Read && this._readOnly)
        return MediaWriteProtected;

      var file = this._filesystem.OpenFile(nodeId, fileAccess);
      try {
        if (mode is FileMode.Create or FileMode.Truncate) {
          if (!Has(FilesystemDriverCapabilities.Truncate))
            return DokanResult.NotImplemented;
          file.SetLength(0);
        }
        info.Context = new DokanOpenHandle(nodeId, node.Kind, file, mode == FileMode.Append);
        file = null;
      } finally {
        file?.Dispose();
      }

      return mode is FileMode.Create or FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public void Cleanup(string fileName, IDokanFileInfo info) {
    if (!info.DeletePending || this._readOnly)
      return;

    try {
      var target = (info.Context as DokanOpenHandle)?.DeleteTarget;
      if (target is null && FilesystemPathResolver.TryResolveParent(this._filesystem, fileName, out var parent, out var name))
        target = new(parent, name, info.IsDirectory);
      if (target is null)
        return;

      if (target.IsDirectory)
        this._filesystem.RemoveDirectory(target.Parent, target.Name);
      else
        this._filesystem.DeleteFile(target.Parent, target.Name);
    } catch {
      // Cleanup cannot report failure to Dokan. DeleteFile/DeleteDirectory have
      // already validated the request; keep teardown safe if media changed.
    }
  }

  public void CloseFile(string fileName, IDokanFileInfo info) {
    if (info.Context is DokanOpenHandle handle)
      handle.Dispose();
    info.Context = null;
  }

  public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info) {
    bytesRead = 0;
    if (offset < 0)
      return DokanResult.InvalidParameter;

    try {
      if (info.Context is DokanOpenHandle { File: { } openedFile }) {
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

      using var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.Read);
      bytesRead = file.Read(offset, buffer);
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info) {
    bytesWritten = 0;
    if (this._readOnly)
      return MediaWriteProtected;
    if (offset < 0)
      return DokanResult.InvalidParameter;
    if (!Has(FilesystemDriverCapabilities.WriteData))
      return DokanResult.NotImplemented;

    try {
      if (info.Context is DokanOpenHandle { File: { } openedFile } opened) {
        var writeOffset = info.WriteToEndOfFile || opened.Append ? openedFile.Length : offset;
        openedFile.Write(writeOffset, buffer);
        bytesWritten = buffer.Length;
        return DokanResult.Success;
      }

      if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId))
        return DokanResult.FileNotFound;
      using var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.ReadWrite);
      file.Write(info.WriteToEndOfFile ? file.Length : offset, buffer);
      bytesWritten = buffer.Length;
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) {
    try {
      if (!Has(FilesystemDriverCapabilities.Flush))
        return DokanResult.Success;
      if (info.Context is DokanOpenHandle { File: { } file })
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

  public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    => this._readOnly ? MediaWriteProtected : DokanResult.NotImplemented;

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
      var nodeId = info.Context is DokanOpenHandle opened
        ? opened.NodeId
        : FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var resolved) ? resolved : default;
      if (nodeId == default)
        return DokanResult.FileNotFound;
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
      this._filesystem.Rename(oldParent, oldLeaf, newParent, newLeaf, replace);
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    => SetLength(fileName, length, info);

  public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    => SetLength(fileName, length, info);

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
      if (isDirectory && this._filesystem.Enumerate(nodeId.Value).Count != 0)
        return DokanResult.AccessDenied;
      if (!isDirectory) {
        using var writable = this._filesystem.OpenFile(nodeId.Value, System.IO.FileAccess.Write);
      }

      var handle = info.Context as DokanOpenHandle;
      if (handle is null) {
        handle = new(nodeId.Value, node.Kind, null, append: false);
        info.Context = handle;
      }
      handle.DeleteTarget = info.DeletePending ? new(parent, name, isDirectory) : null;
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  private NtStatus SetLength(string fileName, long length, IDokanFileInfo info) {
    if (this._readOnly)
      return MediaWriteProtected;
    if (length < 0)
      return DokanResult.InvalidParameter;
    if (!Has(FilesystemDriverCapabilities.Truncate))
      return DokanResult.NotImplemented;

    try {
      if (info.Context is DokanOpenHandle { File: { } openedFile }) {
        openedFile.SetLength(length);
        return DokanResult.Success;
      }
      if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId))
        return DokanResult.FileNotFound;
      using var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.ReadWrite);
      file.SetLength(length);
      return DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  private bool Has(FilesystemDriverCapabilities capability)
    => this._filesystem.Profile.Capabilities.HasFlag(capability);

  private static System.IO.FileAccess RequiredFileAccess(DokanAccess access, FileMode mode) {
    var wantsWrite = (access & DataWriteAccess) != DokanAccess.None || mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate or FileMode.Append;
    var wantsRead = (access & DataReadAccess) != DokanAccess.None;
    return (wantsRead, wantsWrite) switch {
      (true, true) => System.IO.FileAccess.ReadWrite,
      (_, true) => System.IO.FileAccess.Write,
      _ => System.IO.FileAccess.Read,
    };
  }

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

  private sealed class DokanOpenHandle(
    FilesystemNodeId nodeId,
    FilesystemNodeKind kind,
    IFilesystemFileHandle? file,
    bool append
  ) : IDisposable {
    private IFilesystemFileHandle? _file = file;

    public FilesystemNodeId NodeId { get; } = nodeId;
    public FilesystemNodeKind Kind { get; } = kind;
    public bool Append { get; } = append;
    public IFilesystemFileHandle? File => Volatile.Read(ref this._file);
    public DokanDeleteTarget? DeleteTarget { get; set; }

    public void Dispose() => Interlocked.Exchange(ref this._file, null)?.Dispose();
  }

  private sealed record DokanDeleteTarget(FilesystemNodeId Parent, string Name, bool IsDirectory);
}
