using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// A deliberately raw, read-only view of a recognized single-file format which
/// has no archive or filesystem namespace. The original bytes remain visible:
/// interpreting or transcoding media is the descriptor's responsibility.
/// </summary>
internal static class SingleFileMountProjection {
  private const FilesystemDriverCapabilities ReadCapabilities =
    FilesystemDriverCapabilities.EnumerateDirectories |
    FilesystemDriverCapabilities.ReadData |
    FilesystemDriverCapabilities.RandomAccess |
    FilesystemDriverCapabilities.StableNodeIds |
    FilesystemDriverCapabilities.CasePreservingNames;

  private static readonly FilesystemNodeId Root = new(1, 1);
  private static readonly FilesystemNodeId File = new(2, 1);

  public static FilesystemDriverProfile Probe(IFormatDescriptor descriptor, Stream source) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(source);

    var supported = source.CanRead && source.CanSeek;
    return new(
      descriptor.Id,
      "single-file read-only projection",
      supported ? ReadCapabilities : FilesystemDriverCapabilities.None,
      FilesystemMutationModel.None,
      CanMount: supported,
      CanMountWritable: false,
      [
        "The original source is exposed as one read-only file; this does not decode media streams or executable resources.",
        "No mount-grade writer is available for this source.",
      ]);
  }

  public static IFilesystemSession Open(
      IFormatDescriptor descriptor,
      Stream source,
      FilesystemOpenOptions options,
      string? sourceName) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(options);
    if (!options.ReadOnly)
      throw new NotSupportedException("Single-file projections cannot be mounted read-write.");
    var profile = Probe(descriptor, source);
    if (!profile.CanMount)
      throw new NotSupportedException("Single-file projection requires a readable, seekable source.");

    var name = Path.GetFileName(sourceName?.Replace('\\', '/'));
    if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
      name = descriptor.Id + descriptor.DefaultExtension;
    if (name.IndexOfAny(['/', '\\', ':']) >= 0)
      throw new ArgumentException("Mount leaf name contains path separators.", nameof(sourceName));

    var gate = new object();
    return new ReadOnlyFilesystemSnapshotSession(
      profile,
      Root,
      [
        new(Root, Root, "/", FilesystemNodeKind.Directory, 0, 0, LinkCount: 2),
        new(File, Root, name, FilesystemNodeKind.RegularFile, source.Length, source.Length,
          OpenReadHandle: () => new SourceHandle(source, gate)),
      ]);
  }

  private sealed class SourceHandle(Stream source, object gate) : IFilesystemFileHandle {
    private bool _disposed;

    public FilesystemNodeId NodeId => File;
    public long Length {
      get {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (gate) return source.Length;
      }
    }

    public int Read(long offset, Span<byte> destination) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
      lock (gate) {
        if (offset >= source.Length) return 0;
        source.Position = offset;
        return source.Read(destination[..(int)Math.Min(destination.Length, source.Length - offset)]);
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> data) => throw new NotSupportedException("Read-only file.");
    public void SetLength(long length) => throw new NotSupportedException("Read-only file.");
    public void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
  }
}
