#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.MinixFs;

/// <summary>
/// Mirrors the MINIX v1/v2 mount-state protocol: a writable mount clears
/// MINIX_VALID_FS and a clean close restores the state observed at mount time.
/// V3 removed s_state and therefore never uses this wrapper.
/// </summary>
internal sealed class MinixWritableMountStateSession : IFilesystemSession {
  private const long StateOffset = MinixMountedGeometry.SuperblockOffset + 18;

  private readonly IFilesystemSession _inner;
  private readonly Stream _image;
  private readonly ushort _mountState;
  private readonly bool _leaveOpen;
  private bool _disposed;

  private MinixWritableMountStateSession(
      IFilesystemSession inner,
      Stream image,
      ushort mountState,
      bool leaveOpen) {
    _inner = inner;
    _image = image;
    _mountState = mountState;
    _leaveOpen = leaveOpen;
  }

  public static IFilesystemSession Open(
      Stream image,
      FilesystemDriverProfile profile,
      MinixMountedGeometry geometry,
      bool leaveOpen) {
    var savedPosition = image.Position;
    try {
      WriteState(image, (ushort)(geometry.State & ~MinixMountedGeometry.StateValid));
      image.Flush();
      var inner = new MinixFilesystemSession(image, profile, readOnly: false, leaveOpen: true);
      return new MinixWritableMountStateSession(inner, image, geometry.State, leaveOpen);
    } catch {
      try {
        WriteState(image, geometry.State);
        image.Flush();
      } catch { }
      throw;
    } finally {
      if (image.CanSeek) image.Position = savedPosition;
    }
  }

  public FilesystemDriverProfile Profile => _inner.Profile;
  public FilesystemNodeId RootNodeId => _inner.RootNodeId;
  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) => _inner.Stat(nodeId);
  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) => _inner.Lookup(parentDirectory, name);
  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) => _inner.Enumerate(directory);
  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) => _inner.OpenFile(nodeId, access);
  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => _inner.CreateFile(parentDirectory, name);
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => _inner.CreateDirectory(parentDirectory, name);
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => _inner.DeleteFile(parentDirectory, name);
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => _inner.RemoveDirectory(parentDirectory, name);
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace)
    => _inner.Rename(oldParent, oldName, newParent, newName, replace);
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => _inner.CreateHardLink(existingNode, newParent, newName);
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => _inner.CreateSymbolicLink(parentDirectory, name, target);
  public string ReadSymbolicLink(FilesystemNodeId nodeId) => _inner.ReadSymbolicLink(nodeId);
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => _inner.SetMetadata(nodeId, patch);
  public void Flush() => _inner.Flush();
  public IFilesystemTransaction BeginTransaction() => _inner.BeginTransaction();

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Exception? failure = null;
    try {
      _inner.Dispose();
      WriteState(_image, _mountState);
      _image.Flush();
    } catch (Exception e) {
      failure = e;
    } finally {
      if (!_leaveOpen) _image.Dispose();
    }
    if (failure is not null) throw failure;
  }

  private static void WriteState(Stream image, ushort state) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, state);
    image.Position = StateOffset;
    image.Write(bytes);
  }
}