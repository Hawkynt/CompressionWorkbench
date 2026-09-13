#pragma warning disable CS1591
using ImagesAni = FileFormat.Ani;

namespace CompressionWorkbench.FileFormat.Ani;

/// <summary>
/// Reader for Windows animated cursor (<c>.ani</c>) files. ANI is a RIFF container
/// with the form type <c>"ACON"</c>; the animation data lives in a <c>LIST "fram"</c>
/// chunk that contains one <c>"icon"</c> subchunk per frame, each a full CUR file.
/// </summary>
/// <remarks>
/// <para>
/// The RIFF walk, the <c>anih</c> header, the <c>rate</c> and <c>seq </c> chunks and the
/// <c>LIST INFO</c> metadata are <see cref="ImagesAni.AniReader"/> in
/// Hawkynt.FileFormats.Images. What stays here is the shape the archive side lists against, and
/// the unsigned reading of the header fields that the descriptor's metadata file prints.
/// </para>
/// <para>
/// Chunk layout (all IDs are 4 ASCII bytes, all sizes are 32-bit little-endian
/// counting the chunk body only — the ID + size header add 8 bytes):
/// </para>
/// <list type="bullet">
///   <item><c>RIFF</c> header + <c>ACON</c> form type (4 + 4 + 4 bytes).</item>
///   <item><c>anih</c> (36 bytes): animation header — <c>cbSize</c>, number of frames, number of steps, width/height/bpp (unused when the AF_ICON flag, bit 0, is set), number of planes, default jiffies per step, flags.</item>
///   <item><c>LIST fram</c>: wraps the per-frame <c>icon</c> subchunks.</item>
///   <item><c>icon</c> (variable): one CUR file.</item>
///   <item><c>rate</c> (optional, 4 × nSteps bytes): per-step jiffies overriding the default from the header.</item>
///   <item><c>seq </c> (optional, 4 × nSteps bytes): step → frame-index map for non-linear animations.</item>
///   <item><c>LIST INFO</c>: optional ASCII metadata (title, author).</item>
/// </list>
/// </remarks>
public sealed class AniReader {

  /// <summary>
  /// Represents an animation header.
  /// </summary>
  public sealed record AnimationHeader(
    uint CbSize,
    uint NumFrames,
    uint NumSteps,
    uint Width,
    uint Height,
    uint BitsPerPixel,
    uint NumPlanes,
    uint DefaultJiffiesPerStep,
    uint Flags
  );

  /// <summary>
  /// Represents an ani file.
  /// </summary>
  public sealed record AniFile(
    AnimationHeader Header,
    IReadOnlyList<byte[]> Frames,       // each element is the raw bytes of an ICO/CUR sub-file
    IReadOnlyList<uint> Rates,          // per-step duration overrides (jiffies); empty when the chunk is absent
    IReadOnlyList<uint> Sequence,       // step → frame-index map; empty when linear
    string? Title,
    string? Artist
  );

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  /// <exception cref="InvalidDataException">
  /// The data is not a RIFF 'ACON' container, or carries no usable 'anih' chunk.
  /// </exception>
  public static AniFile Read(ReadOnlySpan<byte> data) {
    var ani = ImagesAni.AniReader.FromSpan(data);
    var header = ani.Header;

    return new AniFile(
      Header: new AnimationHeader(
        CbSize: (uint)header.CbSize,
        NumFrames: (uint)header.NumFrames,
        NumSteps: (uint)header.NumSteps,
        Width: (uint)header.Width,
        Height: (uint)header.Height,
        BitsPerPixel: (uint)header.BitCount,
        NumPlanes: (uint)header.NumPlanes,
        DefaultJiffiesPerStep: (uint)header.DisplayRate,
        Flags: (uint)header.Flags),
      // The frames' own bytes, not the parsed pictures: a frame is a whole CUR file and the
      // archive side hands it on, unpacks it, or writes it back untouched.
      Frames: ani.FrameData,
      Rates: _ToUnsigned(ani.Rates),
      Sequence: _ToUnsigned(ani.Sequence),
      Title: ani.Title,
      Artist: ani.Artist);
  }

  /// <summary>An absent chunk is an empty list here rather than a null one.</summary>
  private static IReadOnlyList<uint> _ToUnsigned(int[]? values) {
    if (values == null || values.Length == 0)
      return [];

    var result = new uint[values.Length];
    for (var i = 0; i < values.Length; i++)
      result[i] = (uint)values[i];
    return result;
  }
}
