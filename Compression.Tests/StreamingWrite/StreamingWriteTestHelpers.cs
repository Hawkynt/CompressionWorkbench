using Compression.Registry.Streaming;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Shared helpers for the streaming-write (<c>CreateFromStreams</c>) tests.
/// </summary>
internal static class StreamingWriteTestHelpers {
  /// <summary>
  /// Wraps an in-memory payload in a <see cref="StreamingArchiveInput"/> whose
  /// <c>OpenStream</c> hands back a <see cref="BoundedEntryStream"/> sized to
  /// the payload — the same bounded, forward-streaming primitive a real
  /// large-file source would supply, so the writer cannot over-read.
  /// </summary>
  public static StreamingArchiveInput File(string name, byte[] data)
    => new(name, data.Length, IsDirectory: false,
      OpenStream: () => new BoundedEntryStream(
        new MemoryStream(data, writable: false), data.Length, leaveOpen: false));

  /// <summary>A directory placeholder input.</summary>
  public static StreamingArchiveInput Dir(string name)
    => new(name, 0, IsDirectory: true, OpenStream: () => Stream.Null);

  /// <summary>Deterministic pseudo-random bytes of the given length.</summary>
  public static byte[] Pattern(int length, int seed = 1234) {
    var data = new byte[length];
    var rng = new Random(seed);
    rng.NextBytes(data);
    return data;
  }
}
