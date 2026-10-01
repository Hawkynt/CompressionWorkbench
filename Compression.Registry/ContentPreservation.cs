using System.Security.Cryptography;

namespace Compression.Registry;

/// <summary>
/// The checks every maintenance operation runs before its result may replace the
/// original: the same decoded payload for a single-stream format, the same
/// <see cref="ArchiveSemanticManifest"/> for a container.
/// </summary>
public static class ContentPreservation {

  /// <summary>
  /// Decodes <paramref name="candidate"/> and requires exactly the bytes of
  /// <paramref name="expectedPayload"/>; throws <see cref="NotSupportedException"/> otherwise.
  /// Both streams are read from their current position.
  /// </summary>
  public static void RequireSameDecodedPayload(IStreamFormatOperations ops, Stream candidate, Stream expectedPayload) {
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(candidate);
    ArgumentNullException.ThrowIfNull(expectedPayload);
    using var decoded = RebuildVerb.CreateScratchStream();
    ops.Decompress(candidate, decoded);
    decoded.Position = 0;
    if (!Digest(decoded).AsSpan().SequenceEqual(Digest(expectedPayload)))
      throw new NotSupportedException("The re-encoded stream does not decode to the original payload; refused, nothing was changed.");
  }

  /// <summary>
  /// Decodes both streams and requires the same payload; throws <see cref="NotSupportedException"/> otherwise.
  /// </summary>
  public static void RequireSamePayload(IStreamFormatOperations ops, Stream original, Stream candidate) {
    ArgumentNullException.ThrowIfNull(ops);
    using var decoded = RebuildVerb.CreateScratchStream();
    original.Position = 0;
    ops.Decompress(original, decoded);
    decoded.Position = 0;
    candidate.Position = 0;
    RequireSameDecodedPayload(ops, candidate, decoded);
  }

  private static byte[] Digest(Stream stream) {
    using var sha = SHA256.Create();
    return sha.ComputeHash(stream);
  }
}

/// <summary>
/// Records the original bytes under every write an in-place operation makes, so that the
/// operation can be undone exactly when its result does not verify.
/// </summary>
/// <remarks>
/// Holding the overwritten ranges rather than a copy of the whole image keeps the cost
/// proportional to what the operation touches — a directory pass on a multi-gigabyte
/// volume journals its directory clusters, not the volume.
/// </remarks>
public sealed class InPlacePatchJournal {
  private readonly List<(long Offset, byte[] Original)> _undo = [];

  /// <summary>Number of writes recorded.</summary>
  public int Count => this._undo.Count;

  /// <summary>Saves what lies at <paramref name="offset"/>, then writes <paramref name="data"/> there.</summary>
  public void Write(Stream image, long offset, ReadOnlySpan<byte> data) {
    ArgumentNullException.ThrowIfNull(image);
    if (offset < 0 || offset + data.Length > image.Length)
      throw new ArgumentOutOfRangeException(nameof(offset), "An in-place patch may not grow the image.");
    var original = new byte[data.Length];
    image.Position = offset;
    image.ReadExactly(original);
    if (original.AsSpan().SequenceEqual(data)) return;
    this._undo.Add((offset, original));
    image.Position = offset;
    image.Write(data);
  }

  /// <summary>Puts every recorded range back, latest first.</summary>
  public void Rollback(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    for (var i = this._undo.Count - 1; i >= 0; --i) {
      var (offset, original) = this._undo[i];
      image.Position = offset;
      image.Write(original);
    }
    this._undo.Clear();
    image.Flush();
  }
}
