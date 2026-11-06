#pragma warning disable CS1591
using ImagesAni = FileFormat.Ani;

namespace CompressionWorkbench.FileFormat.Ani;

/// <summary>
/// WORM writer for Windows animated cursor (.ani) RIFF containers. Each input is
/// expected to be a complete CUR (or ICO) file; the writer wraps the inputs in
/// the canonical RIFF "ACON" structure with a 36-byte <c>anih</c> animation
/// header and a <c>LIST "fram"</c> chunk of <c>icon</c> subchunks — one per
/// input frame.
/// </summary>
/// <remarks>
/// The container assembly is <see cref="ImagesAni.AniWriter"/> in Hawkynt.FileFormats.Images.
/// What stays here is this writer's own policy: the frames go in verbatim, the step count comes
/// from the sequence when there is one, and the header's width, height and depth are left at
/// nought because AF_ICON says each frame carries its own.
/// </remarks>
public sealed class AniWriter {

  /// <summary>
  /// Writes an ANI animated cursor to <paramref name="output"/>. Each frame is
  /// taken verbatim from <paramref name="frames"/> (expected to be CUR file
  /// bytes). When <paramref name="rates"/> is non-empty the per-step durations
  /// override the header's default jiffies; when <paramref name="sequence"/> is
  /// non-empty the steps replay frames in a non-linear order.
  /// </summary>
  /// <param name="output">Target stream; not closed by this method.</param>
  /// <param name="frames">Per-frame CUR/ICO blobs; each becomes an <c>icon</c> subchunk.</param>
  /// <param name="rates">Optional per-step jiffies overrides; emits a <c>rate</c> chunk when non-empty.</param>
  /// <param name="sequence">Optional step → frame-index map; emits a <c>seq </c> chunk when non-empty.</param>
  /// <param name="title">Optional INAM title under a <c>LIST INFO</c> chunk.</param>
  /// <param name="artist">Optional IART artist string under <c>LIST INFO</c>.</param>
  /// <param name="defaultJiffies">Default duration per step in 1/60-second
  ///   units. 6 → 100 ms, a sensible default for a slow animation.</param>
  public static void Write(
      Stream output,
      IReadOnlyList<byte[]> frames,
      IReadOnlyList<uint>? rates = null,
      IReadOnlyList<uint>? sequence = null,
      string? title = null,
      string? artist = null,
      uint defaultJiffies = 6) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(frames);
    if (frames.Count == 0)
      throw new ArgumentException("ANI: at least one frame is required.", nameof(frames));

    var steps = sequence is { Count: > 0 } ? sequence.Count : frames.Count;

    var file = new ImagesAni.AniFile {
      Header = new ImagesAni.AniHeader(
        CbSize: ImagesAni.AniHeader.StructSize,
        NumFrames: frames.Count,
        NumSteps: steps,
        // Ignored while AF_ICON is set, which it always is here: every frame is a whole cursor
        // file and states its own size and depth.
        Width: 0,
        Height: 0,
        BitCount: 0,
        NumPlanes: 1,
        DisplayRate: (int)defaultJiffies,
        // The writer below sets AF_ICON, and AF_SEQUENCE when a sequence is actually written.
        Flags: 0),
      // No parsed view is needed: the frames go in exactly as they arrived, and the writer
      // prefers the unparsed bytes whenever they are there.
      FrameData = frames,
      Rates = _ToSigned(rates),
      Sequence = _ToSigned(sequence),
      Title = string.IsNullOrEmpty(title) ? null : title,
      Artist = string.IsNullOrEmpty(artist) ? null : artist,
    };

    var bytes = ImagesAni.AniWriter.ToBytes(file);
    output.Write(bytes, 0, bytes.Length);
  }

  /// <summary>An empty or absent list writes no chunk at all.</summary>
  private static int[]? _ToSigned(IReadOnlyList<uint>? values) {
    if (values is not { Count: > 0 })
      return null;

    var result = new int[values.Count];
    for (var i = 0; i < values.Count; i++)
      result[i] = (int)values[i];
    return result;
  }
}
