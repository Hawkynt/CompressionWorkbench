namespace Compression.Registry;

/// <summary>
/// Verified shrink helper for virtual-disk containers whose safe maintenance
/// boundary is the raw guest disk rather than the filesystem the descriptor may
/// discover inside it.
/// </summary>
public static class RawDiskShrinkRebuilder {
  /// <summary>
  /// Reads the logical guest disk, builds a canonical container, reads the guest
  /// disk back byte-for-byte, and emits the rebuilt image only when it is both
  /// identical and smaller. Any unsupported/malformed profile or rebuild failure
  /// copies the original through unchanged.
  /// </summary>
  public static void Shrink(
      Stream input,
      Stream output,
      Func<Stream, byte[]> readGuestDisk,
      Func<byte[], byte[]> buildContainer,
      Func<Stream, bool>? canRebuild = null) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(readGuestDisk);
    ArgumentNullException.ThrowIfNull(buildContainer);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("Raw-disk shrink requires a readable, seekable input.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Raw-disk shrink requires a writable, seekable output.", nameof(output));

    try {
      input.Position = 0;
      if (canRebuild is not null && !canRebuild(input)) {
        CopyUnchanged(input, output);
        return;
      }

      input.Position = 0;
      var guest = readGuestDisk(input);
      var rebuilt = buildContainer(guest);
      if (rebuilt.LongLength >= input.Length || rebuilt.Length == 0) {
        CopyUnchanged(input, output);
        return;
      }

      using var verify = new MemoryStream(rebuilt, writable: false);
      var roundTrip = readGuestDisk(verify);
      if (!roundTrip.AsSpan().SequenceEqual(guest)) {
        CopyUnchanged(input, output);
        return;
      }

      output.Position = 0;
      output.SetLength(0);
      output.Write(rebuilt);
      output.Position = 0;
    } catch {
      CopyUnchanged(input, output);
    }
  }

  private static void CopyUnchanged(Stream input, Stream output) {
    if (ReferenceEquals(input, output)) {
      input.Position = 0;
      return;
    }

    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
    output.Position = 0;
  }
}
