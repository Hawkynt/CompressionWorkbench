using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Last-resort, lossless read-only namespace for registered formats that do not
/// expose archive entries or a filesystem driver (media, executable objects,
/// streams, and other opaque files). The original bytes remain accessible as one
/// regular file. No host filesystem parser is used.
/// </summary>
internal static class RawContentFilesystemSession {
  private static readonly FilesystemNodeId Root = new(1, 1);
  private static readonly FilesystemNodeId Content = new(2, 1);

  public static FilesystemDriverProfile ProfileFor(IFormatDescriptor descriptor, Stream source)
    => ProfileFor(descriptor.Id, source);

  public static FilesystemDriverProfile ProfileFor(string formatId, Stream source) {
    ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
    ArgumentNullException.ThrowIfNull(source);

    var readable = source.CanRead && source.CanSeek;
    return new FilesystemDriverProfile(
      formatId,
      "opaque source read-only view",
      readable
        ? FilesystemMountCapabilityResolver.CoreReadCapabilities | FilesystemDriverCapabilities.CasePreservingNames
        : FilesystemDriverCapabilities.None,
      FilesystemMutationModel.None,
      CanMount: readable,
      CanMountWritable: false,
      [
        "This registered format exposes no mountable internal namespace. The original file is presented byte-for-byte as one read-only entry.",
        "No media, executable, or image structure is fabricated; a format-specific archive/driver provider takes precedence whenever available.",
      ]);
  }

  public static IFilesystemSession Open(IFormatDescriptor descriptor, Stream source, string? name = null)
    => Open(descriptor.Id, source, name ?? descriptor.Id + descriptor.DefaultExtension);

  public static IFilesystemSession Open(string formatId, Stream source, string? name = null) {
    var profile = ProfileFor(formatId, source);
    if (!profile.CanMount)
      throw new NotSupportedException("An opaque source mount requires a readable, seekable stream.");

    var leaf = string.IsNullOrWhiteSpace(name)
      ? "content.bin"
      : name.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
      leaf = "content.bin";
    var size = source.Length;
    var gate = new object();

    return new ReadOnlyFilesystemSnapshotSession(
      profile,
      Root,
      [
        new(Root, Root, "/", FilesystemNodeKind.Directory, 0, 0, LinkCount: 2),
        new(Content, Root, leaf, FilesystemNodeKind.RegularFile, size, size,
          OpenReadHandle: () => new PositionalSourceHandle(Content, source, gate, size)),
      ]);
  }

  private sealed class PositionalSourceHandle(
      FilesystemNodeId id,
      Stream source,
      object gate,
      long length) : IFilesystemFileHandle {
    private bool _disposed;

    public FilesystemNodeId NodeId { get; } = id;
    public long Length => length;

    public int Read(long offset, Span<byte> destination) {
      ObjectDisposedException.ThrowIf(this._disposed, this);
      if (offset < 0)
        throw new ArgumentOutOfRangeException(nameof(offset));
      if (offset >= length || destination.IsEmpty)
        return 0;

      lock (gate) {
        var saved = source.Position;
        try {
          source.Position = offset;
          return source.Read(destination[..checked((int)Math.Min(destination.Length, length - offset))]);
        } finally {
          source.Position = saved;
        }
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
      => throw new NotSupportedException("Opaque file projections are read-only.");
    public void SetLength(long value)
      => throw new NotSupportedException("Opaque file projections are read-only.");
    public void Flush() => ObjectDisposedException.ThrowIf(this._disposed, this);
    public void Dispose() => this._disposed = true;
  }
}
