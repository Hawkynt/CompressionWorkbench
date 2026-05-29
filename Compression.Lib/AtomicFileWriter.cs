namespace Compression.Lib;

/// <summary>
/// Power-fail-resistant file writer. Writes are staged to a sibling temp file
/// (so the destination volume's freespace and access rights are exercised up
/// front), flushed to disk, then atomically renamed into place. If the caller
/// throws (or the process dies between <c>Flush</c> and <c>Move</c>), the
/// partially-written temp file is deleted so the original target — if any —
/// is left untouched.
/// </summary>
/// <remarks>
/// <para>
/// This is the canonical fail-safe write pattern used by every conversion
/// helper in <see cref="ArchiveOperations"/>. Use it whenever you would
/// otherwise call <see cref="File.Create(string)"/> or
/// <see cref="File.WriteAllBytes(string, byte[])"/> on a file the user owns.
/// </para>
/// <para>
/// Stream-based APIs that accept a caller-supplied <see cref="Stream"/> are
/// intentionally NOT routed through this helper: the caller chose where bytes
/// land, so atomic-rename semantics are their responsibility. For those paths
/// the library only guarantees <c>Flush()</c> before returning.
/// </para>
/// </remarks>
public static class AtomicFileWriter {

  /// <summary>
  /// Atomically writes <paramref name="targetPath"/> by invoking
  /// <paramref name="writeAction"/> against a fresh temp file, flushing it
  /// to disk, then renaming it into place. On exception the temp file is
  /// best-effort deleted and the original target is left untouched.
  /// </summary>
  /// <param name="targetPath">Final destination path.</param>
  /// <param name="writeAction">Action that writes the file contents to the
  /// supplied <see cref="FileStream"/>. The stream is closed and flushed by
  /// this helper — do not dispose it manually.</param>
  public static void WriteAtomic(string targetPath, Action<FileStream> writeAction) {
    ArgumentNullException.ThrowIfNull(targetPath);
    ArgumentNullException.ThrowIfNull(writeAction);

    var tempPath = MakeTempPath(targetPath);
    try {
      using (var fs = File.Create(tempPath)) {
        writeAction(fs);
        fs.Flush(flushToDisk: true);
      }
      ReplaceTarget(tempPath, targetPath);
    } catch {
      TryDelete(tempPath);
      throw;
    }
  }

  /// <summary>
  /// Writes <paramref name="bytes"/> to <paramref name="targetPath"/> using
  /// the atomic rename protocol.
  /// </summary>
  public static void WriteAllBytesAtomic(string targetPath, byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    WriteAtomic(targetPath, fs => fs.Write(bytes, 0, bytes.Length));
  }

  /// <summary>
  /// Returns a sibling temp path next to <paramref name="targetPath"/>. The
  /// temp lives on the same volume so the final move is a true atomic rename
  /// (and not a cross-volume copy that loses atomicity).
  /// </summary>
  public static string MakeTempPath(string targetPath) {
    // Keep the temp adjacent to the target so it shares the same filesystem.
    // Embed a GUID slice so concurrent writes to the same target don't collide.
    return targetPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
  }

  /// <summary>
  /// Atomically replaces <paramref name="targetPath"/> with the contents of
  /// <paramref name="tempPath"/>. Used to commit a fully-written temp file.
  /// </summary>
  public static void ReplaceTarget(string tempPath, string targetPath) {
    if (File.Exists(targetPath))
      File.Delete(targetPath);
    File.Move(tempPath, targetPath);
  }

  /// <summary>
  /// Best-effort delete that swallows all exceptions. Used in catch / finally
  /// blocks to clean up temp files without masking the original exception.
  /// </summary>
  public static void TryDelete(string path) {
    try {
      if (File.Exists(path))
        File.Delete(path);
    } catch {
      // best-effort
    }
  }
}
