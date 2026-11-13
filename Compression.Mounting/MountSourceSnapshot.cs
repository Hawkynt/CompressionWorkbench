namespace Compression.Mounting;

/// <summary>
/// Immutable source image for repeated transactional archive write-backs.
/// Small images stay in memory; large images spool to a temporary file so a
/// small mounted namespace does not require a large compressed image in RAM.
/// </summary>
internal sealed class MountSourceSnapshot : IDisposable {
  private const long MemoryLimit = 16L * 1024 * 1024;
  private readonly byte[]? _bytes;
  private readonly string? _path;
  private bool _disposed;

  private MountSourceSnapshot(byte[] bytes) => this._bytes = bytes;
  private MountSourceSnapshot(string path) => this._path = path;

  public static MountSourceSnapshot Capture(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("Mount snapshots require readable, seekable input.", nameof(input));

    var position = input.Position;
    try {
      input.Position = 0;
      if (input.Length <= MemoryLimit) {
        using var buffer = new MemoryStream(checked((int)input.Length));
        input.CopyTo(buffer);
        return new(buffer.ToArray());
      }

      var path = Path.Combine(Path.GetTempPath(), $"cwb-mount-{Guid.NewGuid():N}.tmp");
      try {
        using (var temporary = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                 FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
          input.CopyTo(temporary);
        return new(path);
      } catch {
        try { File.Delete(path); } catch { }
        throw;
      }
    } finally {
      input.Position = position;
    }
  }

  public Stream OpenRead() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    return this._bytes is { } bytes
      ? new MemoryStream(bytes, writable: false)
      : new FileStream(this._path!, FileMode.Open, FileAccess.Read,
          FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
  }

  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (this._path is not null)
      File.Delete(this._path);
  }
}
