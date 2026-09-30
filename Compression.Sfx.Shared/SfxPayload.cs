using Compression.Registry;

namespace Compression.Sfx;

/// <summary>
/// Finds the archive inside the running self-extractor and hands it out as a stream bounded to
/// exactly the payload bytes.
/// </summary>
public static class SfxPayload {
  /// <summary>
  /// The environment variable a POSIX bootstrap sets to point at the original container.
  /// </summary>
  /// <remarks>
  /// A polyglot SFX runs its POSIX stub from a temporary copy, so <see cref="Environment.ProcessPath"/>
  /// points at that copy, which holds a stub and no payload. The bootstrap passes the real file here.
  /// </remarks>
  public const string SourceVariable = "CWB_SFX_SOURCE";

  /// <summary>The container this process should read its payload from.</summary>
  public static string ResolveSource()
    => Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 } fromBootstrap
      ? fromBootstrap
      : Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the running executable.");

  /// <summary>
  /// Opens the embedded archive. The returned stream starts at the archive's first byte and ends at
  /// its last — never the container's.
  /// </summary>
  /// <param name="container">Kept open for the lifetime of the payload; dispose it afterwards.</param>
  /// <param name="payload">The archive, bounded.</param>
  /// <returns>False when this file carries no archive of ours.</returns>
  /// <remarks>
  /// The bounding is not cosmetic. A reader that locates its directory from the end of the stream —
  /// ZIP scans backwards for the end-of-central-directory record — would otherwise read the SFX
  /// trailer as archive data and fail on the signature.
  /// </remarks>
  public static bool TryOpen(out FileStream container, out Stream payload) {
    container = File.OpenRead(ResolveSource());
    try {
      if (!SfxTrailer.TryRead(container, out var location)) {
        payload = Stream.Null;
        container.Dispose();
        container = null!;
        return false;
      }

      payload = new SubStream(container, location.Offset, location.Length);
      return true;
    } catch {
      container.Dispose();
      container = null!;
      throw;
    }
  }
}
