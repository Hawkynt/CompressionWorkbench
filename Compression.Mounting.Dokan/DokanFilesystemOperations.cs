using System.Security.AccessControl;
using Compression.Registry;
using DokanNet;
using DokanAccess = DokanNet.FileAccess;

namespace Compression.Mounting.Dokan;

/// <summary>
/// Read-only Dokan callback bridge over the mount-grade filesystem contract.
/// Kernel paths are resolved only when a handle is opened; subsequent data I/O
/// uses the stable node/file-handle context carried by Dokan.
/// </summary>
public sealed class DokanFilesystemOperations(IFilesystemSession filesystem) : IDokanOperations {
  private const NtStatus MediaWriteProtected = (NtStatus)0xC00000A2L; // STATUS_MEDIA_WRITE_PROTECTED

  private const DokanAccess MutatingAccess =
    DokanAccess.WriteData |
    DokanAccess.AppendData |
    DokanAccess.WriteExtendedAttributes |
    DokanAccess.DeleteChild |
    DokanAccess.WriteAttributes |
    DokanAccess.Delete |
    DokanAccess.ChangePermissions |
    DokanAccess.SetOwnership |
    DokanAccess.GenericWrite |
    DokanAccess.GenericAll;

  private readonly IFilesystemSession _filesystem = filesystem ?? throw new ArgumentNullException(nameof(filesystem));
  private volatile bool _mounted;
  private string? _mountedTarget;

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
      if (!exists)
        return mode is FileMode.Open ? DokanResult.FileNotFound : MediaWriteProtected;

      if (mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate or FileMode.Append)
        return MediaWriteProtected;
      if ((access & MutatingAccess) != DokanAccess.None)
        return MediaWriteProtected;

      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory) {
        info.IsDirectory = true;
        info.Context = new DokanOpenHandle(nodeId, node.Kind, null);
        return mode == FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;
      }

      if (info.IsDirectory)
        return DokanResult.NotADirectory;
      if (node.Kind != FilesystemNodeKind.RegularFile)
        return DokanResult.NotImplemented;

      var file = this._filesystem.OpenFile(nodeId, System.IO.FileAccess.Read);
      info.Context = new DokanOpenHandle(nodeId, node.Kind, file);
      return mode == FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public void Cleanup(string fileName, IDokanFileInfo info) {
    // A read-only bridge never accepts DeleteFile/DeleteDirectory, so there is
    // deliberately no delete-pending work to perform here.
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

      // Dokan can issue paging reads without the original CreateFile context.
      // Re-open that request positionally rather than introducing a shared
      // Stream.Position fallback.
      if (!FilesystemPathResolver.TryResolve(this._filesystem, fileName, out var nodeId))
        return DokanResult.FileNotFound;

      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory)
        return DokanResult.FileIsADirectory;
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
    return MediaWriteProtected;
  }

  public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) {
    try {
      if (!this._filesystem.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Flush))
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

  public NtStatus FindFilesWithPattern(
    string fileName,
    string searchPattern,
    out IList<FileInformation> files,
    IDokanFileInfo info
  ) {
    files = [];
    return DokanResult.NotImplemented;
  }

  public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    => MediaWriteProtected;

  public NtStatus SetFileTime(
    string fileName,
    DateTime? creationTime,
    DateTime? lastAccessTime,
    DateTime? lastWriteTime,
    IDokanFileInfo info
  ) => MediaWriteProtected;

  public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    => MediaWriteProtected;

  public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    => MediaWriteProtected;

  public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    => MediaWriteProtected;

  public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    => MediaWriteProtected;

  public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    => MediaWriteProtected;

  public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    => DokanResult.NotImplemented;

  public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    => DokanResult.NotImplemented;

  public NtStatus GetDiskFreeSpace(
    out long freeBytesAvailable,
    out long totalNumberOfBytes,
    out long totalNumberOfFreeBytes,
    IDokanFileInfo info
  ) {
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
    features = FileSystemFeatures.ReadOnlyVolume;

    var capabilities = this._filesystem.Profile.Capabilities;
    if (capabilities.HasFlag(FilesystemDriverCapabilities.CaseSensitiveNames))
      features |= FileSystemFeatures.CaseSensitiveSearch;
    if (capabilities.HasFlag(FilesystemDriverCapabilities.CasePreservingNames))
      features |= FileSystemFeatures.CasePreservedNames;

    return DokanResult.Success;
  }

  public NtStatus GetFileSecurity(
    string fileName,
    out FileSystemSecurity? security,
    AccessControlSections sections,
    IDokanFileInfo info
  ) {
    security = null;
    return DokanResult.NotImplemented;
  }

  public NtStatus SetFileSecurity(
    string fileName,
    FileSystemSecurity security,
    AccessControlSections sections,
    IDokanFileInfo info
  ) => MediaWriteProtected;

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

  private static FileInformation ToDokanFileInformation(string name, FilesystemNodeInfo node)
    => new() {
      FileName = name,
      Attributes = ToFileAttributes(node.Kind),
      CreationTime = ToDateTime(node.Created),
      LastAccessTime = ToDateTime(node.Accessed),
      LastWriteTime = ToDateTime(node.Modified),
      Length = node.Kind == FilesystemNodeKind.RegularFile ? node.Size : 0,
    };

  private static FileAttributes ToFileAttributes(FilesystemNodeKind kind)
    => kind switch {
      FilesystemNodeKind.Directory => FileAttributes.Directory | FileAttributes.ReadOnly,
      FilesystemNodeKind.SymbolicLink => FileAttributes.ReparsePoint | FileAttributes.ReadOnly,
      _ => FileAttributes.ReadOnly,
    };

  private static DateTime? ToDateTime(DateTimeOffset? value)
    => value?.UtcDateTime;

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
    IFilesystemFileHandle? file
  ) : IDisposable {
    private IFilesystemFileHandle? _file = file;

    public FilesystemNodeId NodeId { get; } = nodeId;
    public FilesystemNodeKind Kind { get; } = kind;
    public IFilesystemFileHandle? File => Volatile.Read(ref this._file);

    public void Dispose()
      => Interlocked.Exchange(ref this._file, null)?.Dispose();
  }
}
