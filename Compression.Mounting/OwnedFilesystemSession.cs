using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Keeps lower-layer streams/devices alive for exactly as long as the projected
/// namespace. Underlying filesystem implementations are always opened with
/// LeaveOpen=true; this wrapper centralises ownership across nested containers.
/// </summary>
internal sealed class OwnedFilesystemSession(
    IFilesystemSession inner,
    params IDisposable[] owners) : IFilesystemSession {
  private readonly IFilesystemSession _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly IDisposable[] _owners = owners ?? throw new ArgumentNullException(nameof(owners));
  private bool _disposed;

  public FilesystemDriverProfile Profile => this._inner.Profile;
  public FilesystemNodeId RootNodeId => this._inner.RootNodeId;

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) => this._inner.Stat(nodeId);
  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) => this._inner.Lookup(parentDirectory, name);
  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) => this._inner.Enumerate(directory);
  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) => this._inner.OpenFile(nodeId, access);
  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => this._inner.CreateFile(parentDirectory, name);
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => this._inner.CreateDirectory(parentDirectory, name);
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => this._inner.DeleteFile(parentDirectory, name);
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => this._inner.RemoveDirectory(parentDirectory, name);
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace)
    => this._inner.Rename(oldParent, oldName, newParent, newName, replace);
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => this._inner.CreateHardLink(existingNode, newParent, newName);
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => this._inner.CreateSymbolicLink(parentDirectory, name, target);
  public string ReadSymbolicLink(FilesystemNodeId nodeId) => this._inner.ReadSymbolicLink(nodeId);
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => this._inner.SetMetadata(nodeId, patch);
  public void Flush() => this._inner.Flush();
  public IFilesystemTransaction BeginTransaction() => this._inner.BeginTransaction();

  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;

    Exception? first = null;
    try {
      this._inner.Dispose();
    } catch (Exception ex) {
      first = ex;
    }

    for (var i = this._owners.Length - 1; i >= 0; --i) {
      try {
        this._owners[i].Dispose();
      } catch (Exception ex) when (first is not null) {
        // Preserve the first disposal failure while still releasing every layer.
        _ = ex;
      } catch (Exception ex) {
        first = ex;
      }
    }

    if (first is not null)
      throw first;
  }
}
