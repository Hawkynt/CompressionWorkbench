using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// The x86 call-target rewriting a WIM applies to every chunk it compresses with
/// LZX, before compressing and again in reverse after decompressing.
/// </summary>
/// <remarks>
/// <para>An <c>E8</c> byte begins a 32-bit relative call, and in a run of
/// machine code the same routine is called from many places with a different
/// relative offset each time. Turning those offsets into absolute ones makes the
/// repeated calls identical and gives the compressor something to match. It is
/// worth a few percent on an image full of executables and nothing at all on
/// anything else, which is why it is applied unconditionally rather than decided
/// per chunk: a reader undoes it either way.</para>
///
/// <para>That is the part that matters here. A reader assumes the rewriting was
/// done and undoes it, so a chunk compressed without it comes back with any byte
/// sequence that looked like a call quietly altered — not a decoding failure,
/// just different bytes, reported as a hash mismatch on the resource.</para>
///
/// <para>The offsets are chunk-relative, and the magic size below is the one the
/// format fixes rather than the real length of anything. Both are what make a
/// rewritten chunk reversible without recording what was rewritten.</para>
/// </remarks>
public static class LzxWimE8Filter {
  /// <summary>
  /// The file size the translation pretends every image has. Offsets at or
  /// beyond it are left alone, which keeps the rewriting reversible.
  /// </summary>
  private const int MagicFileSize = 12_000_000;

  /// <summary>
  /// The number of bytes at the end of a chunk that are never examined: a call
  /// needs its opcode and four bytes of operand, and the format leaves this much
  /// room rather than the five that implies.
  /// </summary>
  private const int Tail = 10;

  /// <summary>Rewrites call targets from relative to absolute, in place.</summary>
  /// <param name="data">The chunk to rewrite.</param>
  public static void Preprocess(byte[] data) => Filter(data, toAbsolute: true);

  /// <summary>Rewrites call targets from absolute back to relative, in place.</summary>
  /// <param name="data">The chunk to rewrite.</param>
  public static void Postprocess(byte[] data) => Filter(data, toAbsolute: false);

  private static void Filter(byte[] data, bool toAbsolute) {
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length <= Tail)
      return;

    var end = data.Length - Tail;
    for (var at = 0; at < end;) {
      if (data[at] != 0xE8) {
        ++at;
        continue;
      }

      var operand = data.AsSpan(at + 1, 4);
      var value = BinaryPrimitives.ReadInt32LittleEndian(operand);
      var rewritten = toAbsolute ? ToAbsolute(value, at) : ToRelative(value, at);
      if (rewritten is not null)
        BinaryPrimitives.WriteInt32LittleEndian(operand, rewritten.Value);

      at += 5;
    }
  }

  /// <summary>
  /// A relative target that could name somewhere inside the pretended file
  /// becomes absolute; anything else is left as it stands.
  /// </summary>
  private static int? ToAbsolute(int relative, int position) {
    if (relative < -position || relative >= MagicFileSize)
      return null;

    return relative < MagicFileSize - position
      ? relative + position
      : relative - MagicFileSize;
  }

  /// <summary>The reverse of <see cref="ToAbsolute" />.</summary>
  private static int? ToRelative(int absolute, int position) {
    if (absolute >= 0)
      return absolute < MagicFileSize ? absolute - position : null;

    return absolute >= -position ? absolute + MagicFileSize : null;
  }
}
