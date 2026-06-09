#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace CompressionWorkbench.FileFormat.Ani;

/// <summary>
/// WORM writer for Windows animated cursor (.ani) RIFF containers. Each input is
/// expected to be a complete CUR (or ICO) file; the writer wraps the inputs in
/// the canonical RIFF "ACON" structure with a 36-byte <c>anih</c> animation
/// header and a <c>LIST "fram"</c> chunk of <c>icon</c> subchunks — one per
/// input frame.
/// </summary>
/// <remarks>
/// Layout per the documented Windows ANI format:
/// <list type="bullet">
///   <item><c>RIFF</c> 4 + size 4 + <c>ACON</c> 4 — outer wrapper.</item>
///   <item><c>anih</c> + 4 + 36-byte AnimationHeader — frame/step counts, default jiffies, ICON flag.</item>
///   <item>(optional) <c>LIST INFO</c> with <c>INAM</c>/<c>IART</c> when title/artist supplied.</item>
///   <item>(optional) <c>rate</c> chunk with per-step jiffies.</item>
///   <item>(optional) <c>seq </c> chunk with step → frame index map.</item>
///   <item><c>LIST</c> + size + <c>fram</c> + N×(<c>icon</c> + size + body + RIFF pad).</item>
/// </list>
/// All chunk sizes are 32-bit little-endian and exclude the 8-byte ID/size
/// header. Bodies are word-aligned (a NUL pad byte follows odd-length bodies).
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

    // Assemble inner body, then prefix the outer RIFF header.
    using var ms = new MemoryStream();

    // anih chunk.
    var anih = new byte[36];
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(0, 4), 36);                          // cbSize
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(4, 4), (uint)frames.Count);         // nFrames
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(8, 4),
      sequence is { Count: > 0 } ? (uint)sequence.Count : (uint)frames.Count);                // nSteps
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(12, 4), 0);                          // iWidth (ignored when ICON flag set)
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(16, 4), 0);                          // iHeight
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(20, 4), 0);                          // iBitCount
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(24, 4), 1);                          // nPlanes
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(28, 4), defaultJiffies);             // iJifRate
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(32, 4), 0x01u);                      // fl — bit 0 = ICON (frames are CURs)
    WriteChunk(ms, "anih", anih);

    // Optional LIST INFO.
    if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist)) {
      using var info = new MemoryStream();
      info.Write("INFO"u8);
      if (!string.IsNullOrEmpty(title)) WriteChunk(info, "INAM", AsciiCStr(title));
      if (!string.IsNullOrEmpty(artist)) WriteChunk(info, "IART", AsciiCStr(artist));
      WriteChunk(ms, "LIST", info.ToArray());
    }

    // Optional rate chunk.
    if (rates is { Count: > 0 }) {
      var rateBody = new byte[4 * rates.Count];
      for (var i = 0; i < rates.Count; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(rateBody.AsSpan(4 * i, 4), rates[i]);
      WriteChunk(ms, "rate", rateBody);
    }

    // Optional seq chunk (id is literally "seq " — 4 ASCII chars including a trailing space).
    if (sequence is { Count: > 0 }) {
      var seqBody = new byte[4 * sequence.Count];
      for (var i = 0; i < sequence.Count; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(seqBody.AsSpan(4 * i, 4), sequence[i]);
      WriteChunk(ms, "seq ", seqBody);
    }

    // LIST "fram" wrapping one icon subchunk per frame.
    using var fram = new MemoryStream();
    fram.Write("fram"u8);
    foreach (var frame in frames)
      WriteChunk(fram, "icon", frame);
    WriteChunk(ms, "LIST", fram.ToArray());

    var inner = ms.ToArray();

    // Outer RIFF wrapper.
    Span<byte> riffHeader = stackalloc byte[12];
    "RIFF"u8.CopyTo(riffHeader[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(riffHeader.Slice(4, 4), (uint)(4 + inner.Length)); // size includes "ACON" form type + body
    "ACON"u8.CopyTo(riffHeader[8..12]);
    output.Write(riffHeader);
    output.Write(inner, 0, inner.Length);
  }

  private static void WriteChunk(Stream s, string fourCc, byte[] body) {
    if (fourCc.Length != 4) throw new ArgumentException("RIFF chunk id must be 4 chars.", nameof(fourCc));
    Span<byte> hdr = stackalloc byte[8];
    Encoding.ASCII.GetBytes(fourCc, hdr[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], (uint)body.Length);
    s.Write(hdr);
    s.Write(body, 0, body.Length);
    // RIFF word alignment: odd-sized bodies get a NUL pad byte.
    if ((body.Length & 1) != 0) s.WriteByte(0);
  }

  /// <summary>NUL-terminated ASCII, padded to even length per RIFF INFO convention.</summary>
  private static byte[] AsciiCStr(string s) {
    var raw = Encoding.ASCII.GetBytes(s);
    var len = raw.Length + 1;
    var buf = new byte[len];
    raw.CopyTo(buf, 0);
    return buf;
  }
}
