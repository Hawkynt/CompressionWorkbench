using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// One carved hit located in a host stream — possibly with nested children
/// that live inside the unwrapped payload of this hit. Mirrors what tools
/// like binwalk / DiskGenius show: an outer container (e.g. <c>Qcow2</c>)
/// that contains a partition table (<c>MBR</c>) which in turn contains a
/// filesystem (<c>Fat</c>).
/// </summary>
/// <param name="ByteOffset">
/// Offset where this hit starts. <b>Frame is ambiguous:</b> for top-level
/// hits this is an absolute offset in the host stream; for nested children
/// it depends on the carver's recursion strategy — sometimes relative to
/// the parent's unwrapped payload (e.g. file inside an FS), sometimes
/// absolute on the host (e.g. partition inside an MBR sharing the host's
/// address space). Consumers who care about absolute positions must walk
/// the <see cref="EnvelopeStack"/> and resolve frames themselves.
/// </param>
/// <param name="Length">Length of the hit's payload in bytes.</param>
/// <param name="FormatId">Format descriptor ID (matches <see cref="IFormatDescriptor.Id"/>).</param>
/// <param name="Confidence">Magic-match confidence in [0, 1].</param>
/// <param name="Depth">Nesting depth — 0 = host stream root, 1 = nested in a depth-0 hit, etc.</param>
/// <param name="EnvelopeStack">
/// Outermost-first list of FormatIds in the containment chain. The last
/// entry equals <paramref name="FormatId"/>. E.g. for FAT inside MBR
/// inside QCOW2: <c>["Qcow2", "MBR", "Fat"]</c>.
/// </param>
/// <param name="Children">Hits found inside the unwrapped payload of this hit.</param>
public sealed record NestedHit(
  long ByteOffset,
  long Length,
  string FormatId,
  double Confidence,
  int Depth,
  IReadOnlyList<string> EnvelopeStack,
  IReadOnlyList<NestedHit> Children
);

/// <summary>
/// Recursive variant of <see cref="FilesystemCarver"/> that, for each hit,
/// also probes the unwrapped payload for further nested filesystems /
/// containers. Useful for triple-nested images like
/// <c>QCOW2 → MBR → FAT → file.zip</c>.
/// <para>
/// Output frame: top-level hit offsets are absolute on the host stream;
/// nested children inherit whatever frame the underlying carver emits
/// (see <see cref="NestedHit.ByteOffset"/> for the gory details). This
/// implementation calls <see cref="FilesystemCarver"/> on a SubStream of
/// the parent payload, so child offsets are <i>relative to the SubStream</i>
/// — i.e. relative to the parent's unwrapped payload.
/// </para>
/// </summary>
public sealed class RecursiveFilesystemCarver {

  /// <summary>Maximum recursion depth (default 5).</summary>
  public int MaxDepth { get; init; } = 5;

  /// <summary>Minimum confidence to keep a hit.</summary>
  public double MinConfidence { get; init; } = 0.5;

  /// <summary>Underlying filesystem-carver options propagated to every level.</summary>
  public FsCarveOptions InnerOptions { get; init; } = new();

  /// <summary>
  /// Carves the stream and recursively descends into each hit's payload.
  /// Returns the top-level hits with their <see cref="NestedHit.Children"/> populated.
  /// </summary>
  public IReadOnlyList<NestedHit> CarveStream(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    FormatRegistration.EnsureInitialized();

    return CarveAt(stream, baseOffset: 0, length: stream.Length, depth: 0, parentStack: []);
  }

  private IReadOnlyList<NestedHit> CarveAt(
    Stream stream,
    long baseOffset,
    long length,
    int depth,
    IReadOnlyList<string> parentStack
  ) {
    if (depth >= this.MaxDepth || length <= 0) return [];

    var carver = new FilesystemCarver { Options = this.InnerOptions };
    IReadOnlyList<CarvedFilesystem> carved;

    // Carve the (sub)stream relative to baseOffset. Use a SubStream when
    // we're not at the host root so nested offsets are relative to the
    // parent's payload rather than the host stream.
    Stream view = depth == 0 ? stream : new SubStream(stream, baseOffset, length);
    try {
      carved = carver.CarveStream(view);
    } catch {
      return [];
    }

    var hits = new List<NestedHit>(carved.Count);
    foreach (var c in carved) {
      if (c.Confidence < this.MinConfidence) continue;

      var stack = new List<string>(parentStack.Count + 1);
      stack.AddRange(parentStack);
      stack.Add(c.FormatId);

      var size = c.EstimatedSize > 0 ? c.EstimatedSize : Math.Max(0, length - c.ByteOffset);

      // Recurse: probe the unwrapped payload for further hits. We use the
      // raw byte range here (not the unpacked filesystem contents) — that
      // catches partition tables nested in disk images, etc. We strip out
      // children that re-detect the same format at the same relative offset
      // (the carver scans the same magic again from a SubStream window),
      // because that's a no-op layer rather than a real nested mount.
      IReadOnlyList<NestedHit> children = [];
      if (depth + 1 < this.MaxDepth) {
        var raw = CarveAt(view, c.ByteOffset, size, depth + 1, stack);
        children = raw.Where(k => !(k.ByteOffset == c.ByteOffset && string.Equals(k.FormatId, c.FormatId, StringComparison.OrdinalIgnoreCase))).ToList();
      }

      hits.Add(new NestedHit(
        ByteOffset: c.ByteOffset,
        Length: size,
        FormatId: c.FormatId,
        Confidence: c.Confidence,
        Depth: depth,
        EnvelopeStack: stack,
        Children: children
      ));
    }
    return hits;
  }
}
