#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Pickle;

internal static partial class PickleCodec {
  private const byte Proto = 0x80;
  private const byte FrameOpcode = 0x95;
  private const byte Memoize = 0x94;
  private const byte Mark = (byte)'(';
  private const byte Stop = (byte)'.';
  private const byte EmptyDict = (byte)'}';
  private const byte EmptyList = (byte)']';
  private const byte SetItem = (byte)'s';
  private const byte SetItems = (byte)'u';
  private const byte Append = (byte)'a';
  private const byte Appends = (byte)'e';
  private const byte ShortBinUnicode = 0x8c;
  private const byte BinUnicode = (byte)'X';
  private const byte ShortBinBytes = (byte)'C';
  private const byte BinBytes = (byte)'B';
  private const byte NewTrue = 0x88;
  private const byte NewFalse = 0x89;
  private const byte None = (byte)'N';

  private const byte ProtocolVersion = 4;

  /// <summary>CPython's <c>Pickler._BATCHSIZE</c>: the run length at which a container switches
  /// from one-at-a-time <c>SETITEM</c>/<c>APPEND</c> to a marked <c>SETITEMS</c>/<c>APPENDS</c>
  /// group, and the number of entries a single group carries.</summary>
  private const int BatchSize = 1000;

  /// <summary>
  /// Emits the protocol-4 encoding that CPython's own <c>pickle.dumps(obj, protocol=4)</c> emits
  /// for the same object graph: <c>PROTO</c>, framing, and <c>MEMOIZE</c> after every dict, list,
  /// str and bytes -- integers, bools and <c>None</c> are not memoized, matching <c>Pickler.save</c>.
  ///
  /// The one place the two encoders legitimately diverge is back-references. CPython's memo is keyed
  /// on object identity, so a graph that hands the same <c>str</c> object to two slots emits
  /// <c>BINGET</c> the second time; this encoder walks a tree and never emits <c>BINGET</c> at all.
  /// Both are valid pickles of the same value and both load to the same object, but only a graph
  /// whose leaves are pairwise distinct objects is byte-comparable against CPython. The reference
  /// vectors are built accordingly.
  /// </summary>
  public static void Write(Stream output, StructuredNode root) {
    var framer = new Framer(output);
    WriteNode(framer, root);
    framer.Write(Stop);
    framer.EndFraming();
  }

  private static void WriteNode(Framer output, StructuredNode node) {
    // CPython calls commit_frame() at the head of every save(), so frames always break on an
    // object boundary rather than in the middle of one.
    output.CommitFrame();
    switch (node.Kind) {
      case StructuredNodeKind.Object:
        output.Write(EmptyDict);
        output.Write(Memoize);
        WriteItems(output, node.Members.Count, SetItem, SetItems, (target, index) => {
          var member = node.Members[index];
          WriteUnicode(target, member.Key);
          WriteNode(target, member.Value);
        });
        return;
      case StructuredNodeKind.Array:
        output.Write(EmptyList);
        output.Write(Memoize);
        WriteItems(output, node.Items.Count, Append, Appends, (target, index) => WriteNode(target, node.Items[index]));
        return;
      case StructuredNodeKind.Binary:
        WriteBytes(output, node.Data);
        return;
      case StructuredNodeKind.String:
        WriteUnicode(output, Encoding.UTF8.GetString(node.Data));
        return;
      case StructuredNodeKind.Boolean:
        output.Write(node.Data.AsSpan().SequenceEqual("true"u8) ? NewTrue : NewFalse);
        return;
      case StructuredNodeKind.Null:
        output.Write(None);
        return;
      default:
        WriteUnicode(output, Encoding.UTF8.GetString(node.Data));
        return;
    }
  }

  /// <summary>Reproduces CPython's <c>_batch_setitems</c> / <c>_batch_appends</c> chunking.</summary>
  private static void WriteItems(Framer output, int count, byte single, byte batched, Action<Framer, int> writeItem) {
    for (var start = 0; start < count; start += BatchSize) {
      var length = Math.Min(BatchSize, count - start);
      if (length > 1) {
        output.Write(Mark);
        for (var i = 0; i < length; ++i)
          writeItem(output, start + i);
        output.Write(batched);
      } else {
        writeItem(output, start);
        output.Write(single);
      }
    }
  }

  private static void WriteUnicode(Framer output, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    WriteSized(output, bytes, ShortBinUnicode, BinUnicode);
  }

  private static void WriteBytes(Framer output, ReadOnlySpan<byte> data) => WriteSized(output, data, ShortBinBytes, BinBytes);

  private static void WriteSized(Framer output, ReadOnlySpan<byte> payload, byte shortOpcode, byte longOpcode) {
    Span<byte> header = stackalloc byte[5];
    int headerLength;
    if (payload.Length <= byte.MaxValue) {
      header[0] = shortOpcode;
      header[1] = (byte)payload.Length;
      headerLength = 2;
    } else {
      header[0] = longOpcode;
      BinaryPrimitives.WriteUInt32LittleEndian(header[1..], checked((uint)payload.Length));
      headerLength = 5;
    }

    output.WritePayload(header[..headerLength], payload);
    output.Write(Memoize);
  }

  /// <summary>
  /// CPython's <c>pickle._Framer</c>. Opcodes accumulate in a frame buffer; once a frame reaches
  /// <see cref="FrameSizeTarget"/> the next object boundary flushes it behind a <c>FRAME</c> header.
  /// A payload at least that large bypasses the buffer entirely, so a big file never costs a second
  /// copy of itself in memory. Buffers below <see cref="FrameSizeMinimum"/> are emitted bare, which
  /// is why a pickle as small as <c>{}</c> carries no <c>FRAME</c> at all.
  /// </summary>
  private sealed class Framer {
    private const int FrameSizeTarget = 64 * 1024;
    private const int FrameSizeMinimum = 4;

    private readonly Stream _output;
    private readonly MemoryStream _frame = new(FrameSizeTarget);

    public Framer(Stream output) {
      this._output = output;
      output.WriteByte(Proto);
      output.WriteByte(ProtocolVersion);
    }

    public void Write(byte value) => this._frame.WriteByte(value);

    public void WritePayload(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload) {
      if (payload.Length >= FrameSizeTarget) {
        this.CommitFrame(force: true);
        this._output.Write(header);
        this._output.Write(payload);
        return;
      }

      this._frame.Write(header);
      this._frame.Write(payload);
    }

    public void CommitFrame() => this.CommitFrame(force: false);

    public void EndFraming() => this.CommitFrame(force: true);

    private void CommitFrame(bool force) {
      if (this._frame.Length <= 0 || !force && this._frame.Length < FrameSizeTarget)
        return;

      if (this._frame.Length >= FrameSizeMinimum) {
        this._output.WriteByte(FrameOpcode);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(length, checked((ulong)this._frame.Length));
        this._output.Write(length);
      }

      this._frame.Position = 0;
      this._frame.CopyTo(this._output);
      this._frame.SetLength(0);
    }
  }
}
