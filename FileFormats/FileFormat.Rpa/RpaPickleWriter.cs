#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rpa;

/// <summary>
/// Minimal Python pickle protocol-2 emitter sufficient to round-trip the
/// Ren'Py RPA index dictionary <c>{filename: [(offset, length, prefix), ...]}</c>.
/// </summary>
/// <remarks>
/// <para>
/// We deliberately emit the smallest possible opcode subset that the existing
/// <see cref="RpaPickleParser"/> understands and that the reference Ren'Py
/// reader at <c>renpy/loader.py</c> accepts. The byte layout is:
/// </para>
/// <list type="number">
///   <item><c>PROTO 2</c> — declares pickle protocol version 2.</item>
///   <item><c>EMPTY_DICT</c> — pushes an empty <c>{}</c>.</item>
///   <item><c>MARK</c> — saves the stack height for the SETITEMS sweep.</item>
///   <item>For every <c>(path, [(offset, length, prefix)])</c> pair:
///     <list type="bullet">
///       <item><c>SHORT_BINUNICODE</c> / <c>BINUNICODE</c> path string.</item>
///       <item><c>EMPTY_LIST</c></item>
///       <item><c>MARK</c></item>
///       <item><c>BININT</c> offset (XORed with the archive key)</item>
///       <item><c>BININT</c> length (XORed with the archive key)</item>
///       <item><c>SHORT_BINBYTES</c> / <c>BINBYTES</c> prefix</item>
///       <item><c>TUPLE3</c> — wraps the three values into a tuple.</item>
///       <item><c>APPENDS</c> — adds the tuple to the list.</item>
///     </list>
///   </item>
///   <item><c>SETITEMS</c> — populates the dictionary.</item>
///   <item><c>STOP</c></item>
/// </list>
/// <para>
/// The result is fed straight into <c>zlib</c> by <see cref="RpaWriter"/>
/// before being appended at the index offset declared in the archive header.
/// </para>
/// </remarks>
internal static class RpaPickleWriter {

  // Pickle opcodes (mirror of RpaPickleParser to keep the emit-side honest).
  private const byte OP_PROTO = 0x80;
  private const byte OP_STOP = (byte)'.';
  private const byte OP_EMPTY_DICT = (byte)'}';
  private const byte OP_EMPTY_LIST = (byte)']';
  private const byte OP_MARK = (byte)'(';
  private const byte OP_APPENDS = (byte)'e';
  private const byte OP_SETITEMS = (byte)'u';
  private const byte OP_BININT = (byte)'J';
  private const byte OP_TUPLE3 = 0x87;
  private const byte OP_SHORT_BINUNICODE = 0x8C;
  private const byte OP_BINUNICODE = (byte)'X';
  private const byte OP_SHORT_BINBYTES = (byte)'C';
  private const byte OP_BINBYTES = (byte)'B';

  /// <summary>
  /// Encodes the Ren'Py index dictionary as a pickle byte stream.
  /// </summary>
  /// <param name="entries">Entries to serialize.  Offsets and lengths must
  /// already be the <em>plain</em> (unmasked) values; the <paramref name="xorKey"/>
  /// is folded in here.</param>
  /// <param name="xorKey">RPA-3.x obfuscation key; pass <c>0</c> for RPA-2.0.</param>
  public static byte[] Emit(IReadOnlyList<RpaEntry> entries, uint xorKey) {
    using var ms = new MemoryStream();

    // PROTO 2
    ms.WriteByte(OP_PROTO);
    ms.WriteByte(2);

    // EMPTY_DICT
    ms.WriteByte(OP_EMPTY_DICT);

    // MARK + items + SETITEMS — single batch is fine for the few-K entries Ren'Py games carry.
    ms.WriteByte(OP_MARK);
    foreach (var entry in entries) {
      WriteUnicode(ms, entry.Path);

      ms.WriteByte(OP_EMPTY_LIST);

      ms.WriteByte(OP_MARK);
      WriteBinInt(ms, (uint)entry.Offset ^ xorKey);
      WriteBinInt(ms, (uint)entry.Length ^ xorKey);
      WriteBytes(ms, entry.Prefix);
      ms.WriteByte(OP_TUPLE3);

      // Append the single-tuple result to the list.
      ms.WriteByte(OP_APPENDS);
    }
    ms.WriteByte(OP_SETITEMS);
    ms.WriteByte(OP_STOP);

    return ms.ToArray();
  }

  /// <summary>Emits a Python <c>str</c> using the most compact applicable opcode.</summary>
  private static void WriteUnicode(MemoryStream ms, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    if (bytes.Length < 256) {
      ms.WriteByte(OP_SHORT_BINUNICODE);
      ms.WriteByte((byte)bytes.Length);
      ms.Write(bytes);
      return;
    }
    Span<byte> lenBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, bytes.Length);
    ms.WriteByte(OP_BINUNICODE);
    ms.Write(lenBuf);
    ms.Write(bytes);
  }

  /// <summary>Emits a Python <c>bytes</c> blob.</summary>
  private static void WriteBytes(MemoryStream ms, byte[] value) {
    if (value.Length < 256) {
      ms.WriteByte(OP_SHORT_BINBYTES);
      ms.WriteByte((byte)value.Length);
      ms.Write(value);
      return;
    }
    Span<byte> lenBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, value.Length);
    ms.WriteByte(OP_BINBYTES);
    ms.Write(lenBuf);
    ms.Write(value);
  }

  /// <summary>
  /// Emits a 32-bit Python int. Always uses <c>BININT</c> (4-byte little-endian
  /// signed) — the parser sign-extends, but the RPA reader masks back to
  /// <c>uint32</c> so negative-looking values for offsets &gt; 2 GiB are
  /// round-trippable.
  /// </summary>
  private static void WriteBinInt(MemoryStream ms, uint value) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    ms.WriteByte(OP_BININT);
    ms.Write(buf);
  }
}
