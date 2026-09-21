#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Pickle;

internal static partial class PickleCodec {
  public static StructuredNode Read(Stream stream) => new Reader(StructuredArchive.ReadAll(stream)).Run();

  private sealed record GlobalRef(string Module, string Name);

  private sealed class Reader(byte[] data) {
    private readonly byte[] _data = data;
    private readonly List<object?> _stack = [];
    private readonly List<int> _marks = [];
    private readonly Dictionary<int, object?> _memo = [];
    private int _position;
    private int _operations;

    public StructuredNode Run() {
      while (this._position < this._data.Length) {
        if (++this._operations > 5_000_000) throw new InvalidDataException("Pickle exceeds the opcode safety limit.");
        var op = ReadByte();
        switch (op) {
          case 0x80: {
            var protocol = ReadByte();
            if (protocol > 5) throw new InvalidDataException($"Unsupported pickle protocol {protocol}.");
            break;
          }
          case 0x95: {
            var frameLength = ReadUInt64LE();
            if (frameLength > (ulong)(this._data.Length - this._position))
              throw new InvalidDataException("Pickle FRAME extends beyond the input stream.");
            break;
          }
          case (byte)'(': this._marks.Add(this._stack.Count); break;
          case (byte)'.': return this._stack.Count == 0 ? StructuredNode.Null() : AsNode(Pop());
          case (byte)'0': _ = Pop(); break;
          case (byte)'1': _ = PopToMark(); break;
          case (byte)'2': Push(Peek()); break;
          case (byte)'N': Push(StructuredNode.Null()); break;
          case 0x88: Push(StructuredNode.Text(StructuredNodeKind.Boolean, "true", "bool")); break;
          case 0x89: Push(StructuredNode.Text(StructuredNodeKind.Boolean, "false", "bool")); break;
          case (byte)'I': Push(ParseProtocol0Int(ReadLine())); break;
          case (byte)'J': Push(Number(ReadInt32LE().ToString(CultureInfo.InvariantCulture), "int32")); break;
          case (byte)'K': Push(Number(ReadByte().ToString(CultureInfo.InvariantCulture), "uint8")); break;
          case (byte)'M': Push(Number(ReadUInt16LE().ToString(CultureInfo.InvariantCulture), "uint16")); break;
          case (byte)'L': Push(Number(ReadLine().TrimEnd('L'), "long")); break;
          case 0x8a: Push(Number(ReadLongInteger(ReadByte()), "long")); break;
          case 0x8b: Push(Number(ReadLongInteger(ReadInt32LE()), "long")); break;
          case (byte)'F': Push(Number(ReadLine(), "float")); break;
          case (byte)'G': Push(Number(BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64BE())).ToString("R", CultureInfo.InvariantCulture), "float64")); break;
          case (byte)'S': Push(StructuredNode.Text(StructuredNodeKind.String, ParseQuoted(ReadLine()), "string")); break;
          case (byte)'U': Push(ReadLatin1String(ReadByte(), "string")); break;
          case (byte)'T': Push(ReadLatin1String(ReadInt32LE(), "string")); break;
          case (byte)'V': Push(StructuredNode.Text(StructuredNodeKind.String, ReadLine(), "unicode")); break;
          case (byte)'X': Push(ReadUtf8String(ReadInt32LE(), "unicode")); break;
          case 0x8c: Push(ReadUtf8String(ReadByte(), "unicode")); break;
          case 0x8d: Push(ReadUtf8String(ReadLength64(), "unicode")); break;
          case (byte)'C': Push(StructuredNode.Binary(ReadBytes(ReadByte()), "bytes")); break;
          case (byte)'B': Push(StructuredNode.Binary(ReadBytes(ReadInt32LE()), "bytes")); break;
          case 0x8e: Push(StructuredNode.Binary(ReadBytes(ReadLength64()), "bytes")); break;
          case 0x96: Push(StructuredNode.Binary(ReadBytes(ReadLength64()), "bytearray")); break;
          case (byte)']': Push(StructuredNode.Array("list")); break;
          case (byte)'l': Push(ArrayFrom(PopToMark(), "list")); break;
          case (byte)'a': {
            var value = Pop();
            AsArray(Peek()).Items.Add(AsNode(value));
            break;
          }
          case (byte)'e': {
            var values = PopToMark();
            var list = AsArray(Peek());
            foreach (var value in values) list.Items.Add(AsNode(value));
            break;
          }
          case (byte)')': Push(StructuredNode.Array("tuple")); break;
          case (byte)'t': Push(ArrayFrom(PopToMark(), "tuple")); break;
          case 0x85: Push(ArrayFrom([Pop()], "tuple")); break;
          case 0x86: { var b = Pop(); var a = Pop(); Push(ArrayFrom([a, b], "tuple")); break; }
          case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); Push(ArrayFrom([a, b, c], "tuple")); break; }
          case (byte)'}': Push(StructuredNode.Object("dict")); break;
          case (byte)'d': Push(DictFrom(PopToMark())); break;
          case (byte)'s': { var value = Pop(); var key = Pop(); AddMapEntry(AsObject(Peek()), key, value); break; }
          case (byte)'u': {
            var values = PopToMark();
            var dict = AsObject(Peek());
            for (var i = 0; i + 1 < values.Count; i += 2) AddMapEntry(dict, values[i], values[i + 1]);
            break;
          }
          case 0x8f: Push(StructuredNode.Array("set")); break;
          case 0x90: { var values = PopToMark(); var set = AsArray(Peek()); foreach (var value in values) set.Items.Add(AsNode(value)); break; }
          case 0x91: Push(ArrayFrom(PopToMark(), "frozenset")); break;
          case (byte)'q': this._memo[ReadByte()] = Peek(); break;
          case (byte)'r': this._memo[ReadInt32LE()] = Peek(); break;
          case 0x94: this._memo[this._memo.Count] = Peek(); break;
          case (byte)'h': Push(GetMemo(ReadByte())); break;
          case (byte)'j': Push(GetMemo(ReadInt32LE())); break;
          case (byte)'p': this._memo[ParseMemoIndex(ReadLine())] = Peek(); break;
          case (byte)'g': Push(GetMemo(ParseMemoIndex(ReadLine()))); break;
          case (byte)'c': Push(new GlobalRef(ReadLine(), ReadLine())); break;
          case 0x93: { var name = NodeText(Pop()); var module = NodeText(Pop()); Push(new GlobalRef(module, name)); break; }
          case (byte)'R': { var args = Pop(); var callable = Pop(); Push(CallNode("reduce", callable, args)); break; }
          case 0x81: { var args = Pop(); var cls = Pop(); Push(CallNode("newobj", cls, args)); break; }
          case 0x92: {
            var kwargs = Pop(); var args = Pop(); var cls = Pop();
            var call = CallNode("newobj-ex", cls, args);
            call.Add("$kwargs", AsNode(kwargs));
            Push(call);
            break;
          }
          case (byte)'i': {
            var module = ReadLine();
            var name = ReadLine();
            Push(CallNode("inst", new GlobalRef(module, name), ArrayFrom(PopToMark(), "tuple")));
            break;
          }
          case (byte)'o': {
            var values = PopToMark();
            if (values.Count == 0) throw new InvalidDataException("Pickle OBJ has no callable.");
            Push(CallNode("obj", values[0], ArrayFrom(values.Skip(1), "tuple")));
            break;
          }
          case (byte)'b': {
            var state = Pop();
            var instance = AsNode(Pop());
            var wrapper = StructuredNode.Object("pickle-build").Add("$instance", instance).Add("$state", AsNode(state));
            Push(wrapper);
            break;
          }
          case (byte)'P': Push(StructuredNode.Text(StructuredNodeKind.Reference, ReadLine(), "persistent-id")); break;
          case (byte)'Q': Push(StructuredNode.Text(StructuredNodeKind.Reference, NodeText(Pop()), "persistent-id")); break;
          case 0x82: Push(Extension(ReadByte())); break;
          case 0x83: Push(Extension(ReadUInt16LE())); break;
          case 0x84: Push(Extension(ReadInt32LE())); break;
          case 0x97: Push(StructuredNode.Binary([], "out-of-band-buffer")); break;
          case 0x98: { var value = AsNode(Pop()); Push(StructuredNode.Object("readonly-buffer").Add("$buffer", value)); break; }
          default: throw new InvalidDataException($"Unsupported pickle opcode 0x{op:X2} at offset {this._position - 1}.");
        }
      }
      throw new InvalidDataException("Pickle stream ended without STOP opcode.");
    }

    private static StructuredNode ParseProtocol0Int(string value) => value switch {
      "00" => StructuredNode.Text(StructuredNodeKind.Boolean, "false", "bool"),
      "01" => StructuredNode.Text(StructuredNodeKind.Boolean, "true", "bool"),
      _ => Number(value, "int"),
    };

    private static StructuredNode Extension(long code) => StructuredNode.Text(StructuredNodeKind.Reference, code.ToString(CultureInfo.InvariantCulture), "pickle-extension");
    private static StructuredNode Number(string value, string type) => StructuredNode.Text(StructuredNodeKind.Number, value, type);
    private static StructuredNode ArrayFrom(IEnumerable<object?> values, string type) { var result = StructuredNode.Array(type); foreach (var value in values) result.Items.Add(AsNode(value)); return result; }
    private static StructuredNode DictFrom(IReadOnlyList<object?> values) { var result = StructuredNode.Object("dict"); for (var i = 0; i + 1 < values.Count; i += 2) AddMapEntry(result, values[i], values[i + 1]); return result; }
    private static StructuredNode AsArray(object? value) => AsNode(value).Kind == StructuredNodeKind.Array ? AsNode(value) : throw new InvalidDataException("Pickle container opcode expected a sequence.");
    private static StructuredNode AsObject(object? value) => AsNode(value).Kind == StructuredNodeKind.Object ? AsNode(value) : throw new InvalidDataException("Pickle container opcode expected a dict.");

    private static void AddMapEntry(StructuredNode dict, object? key, object? value) {
      var keyNode = AsNode(key);
      if (keyNode.Kind == StructuredNodeKind.String) { dict.Add(Encoding.UTF8.GetString(keyNode.Data), AsNode(value)); return; }
      var pair = StructuredNode.Object("dict-entry").Add("$key", keyNode).Add("$value", AsNode(value));
      dict.Add($"entry-{dict.Members.Count:D6}", pair);
    }

    private static StructuredNode CallNode(string kind, object? callable, object? args)
      => StructuredNode.Object("pickle-" + kind).Add("$callable", AsNode(callable)).Add("$args", AsNode(args));

    private static StructuredNode AsNode(object? value) => value switch {
      StructuredNode node => node,
      GlobalRef global => StructuredNode.Text(StructuredNodeKind.Reference, $"{global.Module}.{global.Name}", "pickle-global"),
      null => StructuredNode.Null(),
      _ => StructuredNode.Text(StructuredNodeKind.Other, value.ToString() ?? "", "pickle-value"),
    };

    private static string NodeText(object? value) => Encoding.UTF8.GetString(AsNode(value).Data);
    private object? GetMemo(int index) => this._memo.TryGetValue(index, out var value) ? value : throw new InvalidDataException($"Pickle references missing memo index {index}.");
    private static int ParseMemoIndex(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 ? index : throw new InvalidDataException("Invalid pickle memo index.");

    private object? Peek() => this._stack.Count == 0 ? throw new InvalidDataException("Pickle stack underflow.") : this._stack[^1];
    private object? Pop() { var value = Peek(); this._stack.RemoveAt(this._stack.Count - 1); return value; }
    private void Push(object? value) => this._stack.Add(value);
    private List<object?> PopToMark() {
      if (this._marks.Count == 0) throw new InvalidDataException("Pickle MARK stack underflow.");
      var mark = this._marks[^1];
      this._marks.RemoveAt(this._marks.Count - 1);
      var count = this._stack.Count - mark;
      var result = this._stack.GetRange(mark, count);
      this._stack.RemoveRange(mark, count);
      return result;
    }

    private StructuredNode ReadUtf8String(int length, string type) { var bytes = ReadBytes(length); try { return StructuredNode.Text(StructuredNodeKind.String, new UTF8Encoding(false, true).GetString(bytes), type); } catch (DecoderFallbackException ex) { throw new InvalidDataException("Pickle unicode string contains invalid UTF-8.", ex); } }
    private StructuredNode ReadLatin1String(int length, string type) => StructuredNode.Text(StructuredNodeKind.String, Encoding.Latin1.GetString(ReadBytes(length)), type);
    private string ReadLongInteger(int length) { if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Pickle LONG integer is unreasonably large."); return new BigInteger(ReadBytes(length), isUnsigned: false, isBigEndian: false).ToString(CultureInfo.InvariantCulture); }

    private byte ReadByte() { if ((uint)this._position >= (uint)this._data.Length) throw new EndOfStreamException("Truncated pickle stream."); return this._data[this._position++]; }
    private byte[] ReadBytes(int length) { if (length < 0 || length > this._data.Length - this._position) throw new EndOfStreamException("Truncated pickle payload."); var result = this._data.AsSpan(this._position, length).ToArray(); this._position += length; return result; }
    private int ReadInt32LE() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
    private ushort ReadUInt16LE() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));
    private ulong ReadUInt64LE() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));
    private ulong ReadUInt64BE() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));
    private int ReadLength64() { var value = ReadUInt64LE(); return value <= int.MaxValue ? (int)value : throw new InvalidDataException("Pickle item is too large for this process."); }
    private string ReadLine() { var start = this._position; while (this._position < this._data.Length && this._data[this._position] != (byte)'\n') ++this._position; if (this._position >= this._data.Length) throw new EndOfStreamException("Truncated pickle line operand."); var result = Encoding.UTF8.GetString(this._data, start, this._position - start); ++this._position; return result; }
    private static string ParseQuoted(string value) { if (value.Length < 2 || value[0] is not ('\'' or '"') || value[^1] != value[0]) throw new InvalidDataException("Unsupported protocol-0 STRING literal."); return value[1..^1].Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal); }
  }
}
