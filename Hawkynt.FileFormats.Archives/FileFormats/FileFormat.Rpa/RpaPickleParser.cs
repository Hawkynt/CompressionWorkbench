#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Rpa;

/// <summary>
/// Minimal Python pickle (protocol 2) walker sufficient to extract the Ren'Py
/// <c>{filename: [(offset, length, prefix), ...]}</c> index. We don't try to be a
/// general pickle VM — we maintain a stack of values and marks and only understand
/// the opcodes Ren'Py actually emits. Any unknown opcode is gracefully skipped when
/// possible; otherwise parsing bails (the caller falls back to listing no entries).
/// </summary>
internal static class RpaPickleParser {

  public static IReadOnlyList<RpaEntry> ParseIndex(byte[] data, uint xorKey) {
    var p = new Parser(data);
    var root = p.Run();
    if (root is not Dictionary<object, object?> dict)
      throw new InvalidDataException("RPA pickle root is not a dict.");

    var result = new List<RpaEntry>(dict.Count);
    foreach (var kvp in dict) {
      var k = kvp.Key;
      var v = kvp.Value;
      if (k is not string name) continue;
      if (v is not List<object?> list || list.Count == 0) continue;
      // Ren'Py uses the FIRST tuple; multi-tuple form is rare but possible
      if (list[0] is not List<object?> tuple) continue;
      if (tuple.Count < 2) continue;

      long offset = ToLong(tuple[0]);
      long length = ToLong(tuple[1]);
      // Ren'Py stores these as uint32 (positive). If BININT sign-extended them, mask to 32 bits.
      offset &= 0xFFFFFFFFL;
      length &= 0xFFFFFFFFL;
      byte[] prefix = [];
      if (tuple.Count >= 3 && tuple[2] is byte[] bp)
        prefix = bp;
      else if (tuple.Count >= 3 && tuple[2] is string sp)
        prefix = Encoding.Latin1.GetBytes(sp);

      if (xorKey != 0) {
        offset ^= xorKey;
        length ^= xorKey;
      }

      // Sanity clamp — corrupt entries should be skipped, not crash
      if (offset < 0 || length < 0 || length > int.MaxValue) continue;

      result.Add(new RpaEntry { Path = name, Offset = offset, Length = length, Prefix = prefix });
    }
    return result;
  }

  private static long ToLong(object? o) => o switch {
    int i => i,
    long l => l,
    uint u => u,
    short s => s,
    byte b => b,
    _ => 0
  };

  // Pickle opcodes we understand
  private const byte OP_MARK = (byte)'(';
  private const byte OP_STOP = (byte)'.';
  private const byte OP_POP = (byte)'0';
  private const byte OP_POP_MARK = (byte)'1';
  private const byte OP_DUP = (byte)'2';
  private const byte OP_BININT = (byte)'J';
  private const byte OP_BININT1 = (byte)'K';
  private const byte OP_BININT2 = (byte)'M';
  private const byte OP_NONE = (byte)'N';
  private const byte OP_BINUNICODE = (byte)'X';
  private const byte OP_APPEND = (byte)'a';
  private const byte OP_APPENDS = (byte)'e';
  private const byte OP_LIST = (byte)'l';
  private const byte OP_EMPTY_LIST = (byte)']';
  private const byte OP_TUPLE = (byte)'t';
  private const byte OP_TUPLE1 = 0x85;
  private const byte OP_TUPLE2 = 0x86;
  private const byte OP_TUPLE3 = 0x87;
  private const byte OP_EMPTY_DICT = (byte)'}';
  private const byte OP_DICT = (byte)'d';
  private const byte OP_SETITEM = (byte)'s';
  private const byte OP_SETITEMS = (byte)'u';
  private const byte OP_PROTO = 0x80;
  private const byte OP_NEWOBJ = 0x81;
  private const byte OP_FRAME = 0x95;
  private const byte OP_SHORT_BINUNICODE = 0x8C;
  private const byte OP_BINUNICODE8 = 0x8D;
  private const byte OP_SHORT_BINSTRING = (byte)'U';
  private const byte OP_BINSTRING = (byte)'T';
  private const byte OP_SHORT_BINBYTES = (byte)'C';
  private const byte OP_BINBYTES = (byte)'B';
  private const byte OP_BINBYTES8 = 0x8E;
  private const byte OP_MEMOIZE = 0x94;
  private const byte OP_BINPUT = (byte)'q';
  private const byte OP_LONG_BINPUT = (byte)'r';
  private const byte OP_BINGET = (byte)'h';
  private const byte OP_LONG_BINGET = (byte)'j';
  private const byte OP_GLOBAL = (byte)'c';
  private const byte OP_STACK_GLOBAL = 0x93;
  private const byte OP_REDUCE = (byte)'R';
  private const byte OP_BUILD = (byte)'b';
  private const byte OP_INST = (byte)'i';
  private const byte OP_OBJ = (byte)'o';
  private const byte OP_EMPTY_TUPLE = (byte)')';
  private const byte OP_NEWTRUE = 0x88;
  private const byte OP_NEWFALSE = 0x89;
  private const byte OP_LONG1 = 0x8A;
  private const byte OP_LONG4 = 0x8B;

  private sealed class Parser {
    private readonly byte[] _data;
    private int _pos;
    private readonly Stack<object?> _stack = new();
    private readonly Stack<int> _marks = new();
    private readonly Dictionary<int, object?> _memo = new();

    public Parser(byte[] data) { this._data = data; }

    public object? Run() {
      while (this._pos < this._data.Length) {
        byte op = this._data[this._pos++];
        switch (op) {
          case OP_PROTO: this._pos++; break; // skip proto version byte
          case OP_FRAME: this._pos += 8; break;
          case OP_MARK: this._marks.Push(this._stack.Count); break;
          case OP_STOP: return this._stack.Count > 0 ? this._stack.Pop() : null;
          case OP_POP: if (this._stack.Count > 0) this._stack.Pop(); break;
          case OP_POP_MARK: PopToMark(); break;
          case OP_DUP: this._stack.Push(this._stack.Peek()); break;
          case OP_NONE: this._stack.Push(null); break;
          case OP_NEWTRUE: this._stack.Push(true); break;
          case OP_NEWFALSE: this._stack.Push(false); break;
          case OP_BININT: this._stack.Push(ReadInt32LE()); break;
          case OP_BININT1: this._stack.Push((int)this._data[this._pos++]); break;
          case OP_BININT2: this._stack.Push(ReadUInt16LE()); break;
          case OP_LONG1: {
            int n = this._data[this._pos++];
            this._stack.Push(ReadLongBytes(n));
            break;
          }
          case OP_LONG4: {
            int n = ReadInt32LE();
            this._stack.Push(ReadLongBytes(n));
            break;
          }
          case OP_SHORT_BINUNICODE: {
            int n = this._data[this._pos++];
            this._stack.Push(Encoding.UTF8.GetString(this._data, this._pos, n));
            this._pos += n;
            break;
          }
          case OP_BINUNICODE: {
            int n = ReadInt32LE();
            this._stack.Push(Encoding.UTF8.GetString(this._data, this._pos, n));
            this._pos += n;
            break;
          }
          case OP_BINUNICODE8: {
            long n = ReadInt64LE();
            this._stack.Push(Encoding.UTF8.GetString(this._data, this._pos, (int)n));
            this._pos += (int)n;
            break;
          }
          case OP_SHORT_BINSTRING: {
            int n = this._data[this._pos++];
            this._stack.Push(Encoding.Latin1.GetString(this._data, this._pos, n));
            this._pos += n;
            break;
          }
          case OP_BINSTRING: {
            int n = ReadInt32LE();
            this._stack.Push(Encoding.Latin1.GetString(this._data, this._pos, n));
            this._pos += n;
            break;
          }
          case OP_SHORT_BINBYTES: {
            int n = this._data[this._pos++];
            var b = new byte[n];
            Array.Copy(this._data, this._pos, b, 0, n);
            this._pos += n;
            this._stack.Push(b);
            break;
          }
          case OP_BINBYTES: {
            int n = ReadInt32LE();
            var b = new byte[n];
            Array.Copy(this._data, this._pos, b, 0, n);
            this._pos += n;
            this._stack.Push(b);
            break;
          }
          case OP_BINBYTES8: {
            long n = ReadInt64LE();
            var b = new byte[(int)n];
            Array.Copy(this._data, this._pos, b, 0, (int)n);
            this._pos += (int)n;
            this._stack.Push(b);
            break;
          }
          case OP_EMPTY_LIST: this._stack.Push(new List<object?>()); break;
          case OP_EMPTY_DICT: this._stack.Push(new Dictionary<object, object?>()); break;
          case OP_EMPTY_TUPLE: this._stack.Push(new List<object?>()); break;
          case OP_APPEND: {
            var v = this._stack.Pop();
            if (this._stack.Peek() is List<object?> list) list.Add(v);
            break;
          }
          case OP_APPENDS: {
            var items = PopToMark();
            if (this._stack.Peek() is List<object?> list) list.AddRange(items);
            break;
          }
          case OP_LIST: {
            var items = PopToMark();
            this._stack.Push(new List<object?>(items));
            break;
          }
          case OP_TUPLE: {
            var items = PopToMark();
            this._stack.Push(new List<object?>(items));
            break;
          }
          case OP_TUPLE1: {
            var a = this._stack.Pop();
            this._stack.Push(new List<object?> { a });
            break;
          }
          case OP_TUPLE2: {
            var b = this._stack.Pop();
            var a = this._stack.Pop();
            this._stack.Push(new List<object?> { a, b });
            break;
          }
          case OP_TUPLE3: {
            var c = this._stack.Pop();
            var b = this._stack.Pop();
            var a = this._stack.Pop();
            this._stack.Push(new List<object?> { a, b, c });
            break;
          }
          case OP_SETITEM: {
            var v = this._stack.Pop();
            var k = this._stack.Pop();
            if (this._stack.Peek() is Dictionary<object, object?> d && k != null) d[k] = v;
            break;
          }
          case OP_SETITEMS: {
            var items = PopToMark();
            if (this._stack.Peek() is Dictionary<object, object?> d) {
              for (int i = 0; i + 1 < items.Count; i += 2) {
                if (items[i] != null) d[items[i]!] = items[i + 1];
              }
            }
            break;
          }
          case OP_DICT: {
            var items = PopToMark();
            var d = new Dictionary<object, object?>();
            for (int i = 0; i + 1 < items.Count; i += 2) {
              if (items[i] != null) d[items[i]!] = items[i + 1];
            }
            this._stack.Push(d);
            break;
          }
          case OP_MEMOIZE: this._memo[this._memo.Count] = this._stack.Peek(); break;
          case OP_BINPUT: this._memo[this._data[this._pos++]] = this._stack.Peek(); break;
          case OP_LONG_BINPUT: this._memo[ReadInt32LE()] = this._stack.Peek(); break;
          case OP_BINGET: this._stack.Push(this._memo.TryGetValue(this._data[this._pos++], out var gv1) ? gv1 : null); break;
          case OP_LONG_BINGET: this._stack.Push(this._memo.TryGetValue(ReadInt32LE(), out var gv2) ? gv2 : null); break;
          case OP_GLOBAL: {
            // Skip two newline-terminated strings
            SkipLine(); SkipLine();
            this._stack.Push(null);
            break;
          }
          case OP_STACK_GLOBAL: {
            this._stack.Pop(); this._stack.Pop();
            this._stack.Push(null);
            break;
          }
          case OP_REDUCE: {
            // Pop args and callable, push null — Ren'Py index is plain data so this is tolerated
            this._stack.Pop();
            this._stack.Pop();
            this._stack.Push(null);
            break;
          }
          case OP_BUILD: this._stack.Pop(); break;
          case OP_NEWOBJ: {
            this._stack.Pop(); this._stack.Pop();
            this._stack.Push(null);
            break;
          }
          case OP_INST: SkipLine(); SkipLine(); PopToMark(); this._stack.Push(null); break;
          case OP_OBJ: PopToMark(); this._stack.Push(null); break;
          default:
            throw new InvalidDataException($"Unknown pickle opcode 0x{op:X2} at pos {this._pos - 1}.");
        }
      }
      return this._stack.Count > 0 ? this._stack.Pop() : null;
    }

    private List<object?> PopToMark() {
      if (this._marks.Count == 0) return [];
      int mark = this._marks.Pop();
      var arr = this._stack.Reverse().Skip(mark).ToList(); // stack is LIFO; Reverse = bottom→top
      // Actually simpler: pop stack.Count - mark items
      int toPop = this._stack.Count - mark;
      var result = new object?[toPop];
      for (int i = toPop - 1; i >= 0; i--) result[i] = this._stack.Pop();
      return [.. result];
    }

    private int ReadInt32LE() {
      int v = this._data[this._pos] | (this._data[this._pos + 1] << 8) |
              (this._data[this._pos + 2] << 16) | (this._data[this._pos + 3] << 24);
      this._pos += 4;
      return v;
    }

    private int ReadUInt16LE() {
      int v = this._data[this._pos] | (this._data[this._pos + 1] << 8);
      this._pos += 2;
      return v;
    }

    private long ReadInt64LE() {
      long v = 0;
      for (int i = 0; i < 8; i++) v |= ((long)this._data[this._pos + i]) << (i * 8);
      this._pos += 8;
      return v;
    }

    private long ReadLongBytes(int n) {
      // Little-endian two's-complement. For RPA we only see small positive ints.
      long v = 0;
      for (int i = 0; i < n; i++) v |= ((long)this._data[this._pos + i]) << (i * 8);
      // Sign-extend if top bit set
      if (n > 0 && n < 8 && (this._data[this._pos + n - 1] & 0x80) != 0) {
        long mask = -1L << (n * 8);
        v |= mask;
      }
      this._pos += n;
      return v;
    }

    private void SkipLine() {
      while (this._pos < this._data.Length && this._data[this._pos] != (byte)'\n') this._pos++;
      if (this._pos < this._data.Length) this._pos++;
    }
  }
}
