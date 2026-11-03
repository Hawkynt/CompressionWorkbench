using System.Buffers.Binary;

namespace FileFormat.InstallerVise;

/// <summary>
/// Locating the 256-byte substitution permutation every VISE member passes through: in the
/// classic resource fork (<c>DATA 0</c>, literal or A89F000C-packed with a <c>CODE</c> word
/// dictionary, then expanded by its initializer program) or in the packed-data section of the
/// PEF application (VISE 7.3 and 8).
/// </summary>
/// <remarks>Converted from unvise's <c>init.c</c>, <c>io.c</c> (resource map) and <c>extract.c</c> (MIT); see THIRD-PARTY-NOTICE.unvise.txt.</remarks>
internal static class ViseTables {
  private static uint Be32(ReadOnlySpan<byte> b, long off) {
    if (off < 0 || off + 4 > b.Length)
      throw new InvalidDataException("VISE: truncated 32-bit field.");
    return BinaryPrimitives.ReadUInt32BigEndian(b[(int)off..]);
  }

  private static ushort Be16(ReadOnlySpan<byte> b, long off) {
    if (off < 0 || off + 2 > b.Length)
      throw new InvalidDataException("VISE: truncated 16-bit field.");
    return BinaryPrimitives.ReadUInt16BigEndian(b[(int)off..]);
  }

  private static int ResourceCount(ushort minusOne) => minusOne == ushort.MaxValue ? 0 : minusOne + 1;

  /// <summary>The body of resource <paramref name="type"/> <paramref name="wanted"/> in a resource fork, or null.</summary>
  public static byte[]? FindResource(byte[] r, string type, int wanted) {
    long db = Be32(r, 0), mb = Be32(r, 4), ml = Be32(r, 12);
    var mapFields = mb + 24;
    if (mb + ml > r.Length || mapFields + 4 > r.Length)
      throw new InvalidDataException("VISE: bad resource map.");
    var tl = mb + Be16(r, mapFields);
    var tc = ResourceCount(Be16(r, tl));
    for (var ti = 0; ti < tc; ++ti) {
      var te = tl + 2 + ti * 8L;
      if (te + 8 > r.Length)
        throw new InvalidDataException("VISE: truncated resource type list.");
      if (r[(int)te] != type[0] || r[(int)te + 1] != type[1] || r[(int)te + 2] != type[2] || r[(int)te + 3] != type[3])
        continue;
      var count = ResourceCount(Be16(r, te + 4));
      var refs = tl + Be16(r, te + 6);
      for (var i = 0; i < count; ++i) {
        var reference = refs + i * 12L;
        if (reference + 12 > r.Length)
          throw new InvalidDataException("VISE: truncated resource reference.");
        if ((short)Be16(r, reference) != wanted)
          continue;
        var rel = (r[(int)reference + 5] << 16) | (r[(int)reference + 6] << 8) | r[(int)reference + 7];
        var data = db + rel;
        var n = Be32(r, data);
        if (data + 4 + n > r.Length)
          throw new InvalidDataException("VISE: resource data outside the fork.");
        return r.AsSpan((int)(data + 4), (int)n).ToArray();
      }
    }
    return null;
  }

  /// <summary>Expands an A89F000C packed-code stream with its <c>CODE</c> word dictionary.</summary>
  public static byte[] UnpackCode(byte[] p, byte[] code) {
    if (Be32(p, 0) != 0xA89F000C)
      throw new InvalidDataException("VISE: bad packed-code magic.");
    uint outN = Be32(p, 8), ctrl32 = Be32(p, 16), flags = Be32(p, 20);
    if ((flags & 0xFFFFFFFC) != 0x80000000)
      throw new NotSupportedException("VISE: unsupported packed-code flags.");
    long ctrl = ctrl32, lit = 24, op = 0;
    long dict = 0x4FE + Be16(code, 0x4FE + 6 + (flags & 3) * 2);
    var output = new byte[outN];

    ReadOnlySpan<byte> Word(byte[] b, long o) {
      if (o < 0 || o + 2 > b.Length)
        throw new InvalidDataException("VISE: packed-code word past end.");
      return b.AsSpan((int)o, 2);
    }
    void Emit(ReadOnlySpan<byte> w) {
      if (op >= output.Length)
        return;
      output[op++] = w[0];
      if (op < output.Length)
        output[op++] = w[1];
    }
    void Literal() {
      Emit(Word(p, lit));
      lit += 2;
    }
    int Next() {
      if (ctrl >= p.Length)
        throw new InvalidDataException("VISE: packed-code control past end.");
      return p[ctrl++];
    }

    while (op < output.Length) {
      var v = (uint)Next();
      if ((v & 1) == 0) {
        Emit(Word(code, dict + (v >> 1) * 2));
        continue;
      }
      v >>= 1;
      if ((v & 1) != 0) {
        v >>= 1;
        if ((v & 1) == 0) {
          v >>= 1;
          var index = ((uint)Next() << 5) | v;
          index = (index + 0x80) * 2;
          if ((index & 0x2000) != 0)
            Literal();
          Emit(Word(code, dict + (index & ~0x2000u)));
        } else {
          v >>= 1;
          if ((v & 1) != 0) {
            var count = (v >> 1) + 1;
            while (count-- > 0)
              Literal();
          } else {
            var count = (v >> 1) + 2;
            uint raw = Be16(p, ctrl);
            ctrl += 2;
            if ((raw & 0x8000) != 0)
              Literal();
            long src = (raw * 2) & 0xFFFF;
            while (count-- > 0) {
              Emit(Word(output, src));
              src += 2;
            }
          }
        }
      } else {
        v >>= 1;
        var count = (v & 7) + 2;
        v >>= 3;
        var distance = ((uint)Next() << 3) | v;
        var bytes = (distance + 1L) * 2;
        if (bytes > op)
          throw new InvalidDataException("VISE: packed-code back-reference before output.");
        var src = op - bytes;
        while (count-- > 0) {
          Emit(Word(output, src));
          src += 2;
        }
      }
    }
    return output;
  }

  /// <summary>How many 256-byte permutations <paramref name="b"/> contains; the last one goes to <paramref name="table"/>.</summary>
  public static int FindPermutation(ReadOnlySpan<byte> b, byte[] table) {
    var matches = 0;
    if (b.Length < 256)
      return 0;
    Span<bool> seen = stackalloc bool[256];
    for (var start = 0; start <= b.Length - 256; ++start) {
      seen.Clear();
      int i;
      for (i = 0; i < 256; ++i) {
        var value = b[start + i];
        if (seen[value])
          break;
        seen[value] = true;
      }
      if (i == 256) {
        b.Slice(start, 256).CopyTo(table);
        ++matches;
      }
    }
    return matches;
  }

  private static long PefValue(ReadOnlySpan<byte> packed, ref int position) {
    long value = 0;
    for (;;) {
      if (position >= packed.Length || value > (long.MaxValue >> 7))
        throw new InvalidDataException("VISE: invalid PEF packed-data value.");
      var b = packed[position++];
      value = (value << 7) | (b & 0x7Fu);
      if ((b & 0x80) == 0)
        return value;
    }
  }

  /// <summary>Expands a PEF packed-data section (Apple's pattern-initialization opcodes).</summary>
  private static byte[] UnpackPefData(ReadOnlySpan<byte> packed, long expandedSize) {
    var output = new byte[expandedSize];
    var ip = 0;
    long op = 0;
    void Space(long n) {
      if (n > expandedSize - op)
        throw new InvalidDataException("VISE: PEF packed-data output overflow.");
    }
    while (op < expandedSize) {
      if (ip >= packed.Length)
        throw new InvalidDataException("VISE: truncated PEF packed-data section.");
      var control = packed[ip++];
      var opcode = control >> 5;
      long count = control & 31;
      if (count == 0)
        count = PefValue(packed, ref ip);
      if (count == 0)
        throw new InvalidDataException("VISE: zero-length PEF packed-data operation.");
      switch (opcode) {
        case 0:
          Space(count);
          op += count;
          break;
        case 1:
          if (count > packed.Length - ip)
            throw new InvalidDataException("VISE: truncated PEF packed-data section.");
          Space(count);
          packed.Slice(ip, (int)count).CopyTo(output.AsSpan((int)op));
          ip += (int)count;
          op += count;
          break;
        case 2: {
            var repeats = PefValue(packed, ref ip) + 1;
            if (count > packed.Length - ip)
              throw new InvalidDataException("VISE: truncated PEF packed-data section.");
            if (repeats > (expandedSize - op) / count)
              throw new InvalidDataException("VISE: PEF repeated block overflow.");
            for (var i = 0L; i < repeats; ++i) {
              packed.Slice(ip, (int)count).CopyTo(output.AsSpan((int)op));
              op += count;
            }
            ip += (int)count;
            break;
          }
        case 3:
        case 4: {
            var custom = PefValue(packed, ref ip);
            var repeats = PefValue(packed, ref ip);
            var common = ip;
            if (opcode == 3) {
              if (count > packed.Length - ip)
                throw new InvalidDataException("VISE: truncated PEF packed-data section.");
              ip += (int)count;
            }
            for (var i = 0L; i < repeats; ++i) {
              Space(count);
              if (opcode == 3)
                packed.Slice(common, (int)count).CopyTo(output.AsSpan((int)op));
              op += count;
              if (custom > packed.Length - ip)
                throw new InvalidDataException("VISE: truncated PEF packed-data section.");
              Space(custom);
              packed.Slice(ip, (int)custom).CopyTo(output.AsSpan((int)op));
              ip += (int)custom;
              op += custom;
            }
            Space(count);
            if (opcode == 3)
              packed.Slice(common, (int)count).CopyTo(output.AsSpan((int)op));
            op += count;
            break;
          }
        default:
          throw new InvalidDataException("VISE: reserved PEF packed-data opcode.");
      }
    }
    return output;
  }

  /// <summary>Finds the permutation in the packed-data sections of every PEF container in the data fork.</summary>
  public static bool PefTable(byte[] data, byte[] table) {
    var found = false;
    var candidate = new byte[256];
    ReadOnlySpan<byte> joy = "Joy!peff"u8;
    for (var at = data.AsSpan().IndexOf(joy); at >= 0;) {
      long b = at;
      var sections = Be16(data, b + 32);
      if (sections > 0 && sections <= 64 && b + 40 + sections * 28L <= data.Length) {
        for (var i = 0; i < sections; ++i) {
          var header = b + 40 + i * 28L;
          if (data[header + 24] != 2)
            continue;
          long expanded = Be32(data, header + 12), packedSize = Be32(data, header + 16), packedOffset = Be32(data, header + 20);
          if (b + packedOffset + packedSize > data.Length)
            throw new InvalidDataException("VISE: PEF packed-data section outside installer.");
          var unpacked = UnpackPefData(data.AsSpan((int)(b + packedOffset), (int)packedSize), expanded);
          var matches = FindPermutation(unpacked, candidate);
          if (matches > 1)
            throw new InvalidDataException("VISE: PEF packed-data section has multiple substitution-table candidates.");
          if (matches == 1) {
            if (found && !table.AsSpan().SequenceEqual(candidate))
              throw new InvalidDataException("VISE: PEF applications contain different substitution tables.");
            candidate.CopyTo(table, 0);
            found = true;
          }
        }
      }
      var next = data.AsSpan(at + 1).IndexOf(joy);
      at = next < 0 ? -1 : at + 1 + next;
    }
    return found;
  }

  /// <summary>
  /// Runs the three initializer programs of an expanded <c>DATA 0</c> over a model of the signed
  /// 16-bit A5-relative space and takes its sole fully initialized 256-byte permutation.
  /// </summary>
  public static void Data0Table(byte[] d, byte[] table) {
    var mem = new byte[65536];
    var set = new bool[65536];
    long src = 4;
    for (var section = 0; section < 3; ++section) {
      var dst = (int)Be32(d, src);
      src += 4;
      void Store(int v) {
        var ix = (ushort)(dst + 32768);
        mem[ix] = (byte)v;
        set[ix] = true;
        ++dst;
      }
      void Copy(int n) {
        while (n-- > 0) {
          if (src >= d.Length)
            throw new InvalidDataException("VISE: truncated DATA 0 literal.");
          Store(d[src++]);
        }
      }
      for (;;) {
        if (src >= d.Length)
          throw new InvalidDataException("VISE: truncated DATA 0 command.");
        int c = d[src++];
        if (c == 0)
          break;
        if (c == 1) {
          dst += 4;
          Store(0xFF);
          Store(0xFF);
          Copy(2);
        } else if (c == 2) {
          dst += 4;
          Store(0xFF);
          Copy(3);
        } else if (c == 3) {
          Store(0xA9);
          Store(0xF0);
          dst += 2;
          Copy(2);
          ++dst;
          Copy(1);
        } else if (c == 4) {
          Store(0xA9);
          Store(0xF0);
          ++dst;
          Copy(3);
          ++dst;
          Copy(1);
        } else if ((c & 0x80) != 0)
          Copy((c & 0x7F) + 1);
        else if ((c & 0x40) != 0)
          dst += (c & 0x3F) + 1;
        else if ((c & 0x20) != 0) {
          if (src >= d.Length)
            throw new InvalidDataException("VISE: truncated DATA 0 fill.");
          int n = (c & 31) + 2, v = d[src++];
          while (n-- > 0)
            Store(v);
        } else if ((c & 0x10) != 0) {
          var n = (c & 15) + 1;
          while (n-- > 0)
            Store(0xFF);
        } else
          throw new InvalidDataException("VISE: unknown DATA 0 initializer command.");
      }
    }
    var matches = 0;
    var candidate = new byte[256];
    for (var start = 0; start <= 65536 - 256; ++start) {
      if (!set[start] || FindPermutation(mem.AsSpan(start, 256), candidate) != 1)
        continue;
      var allSet = true;
      for (var i = 0; i < 256 && allSet; ++i)
        allSet = set[start + i];
      if (allSet) {
        candidate.CopyTo(table, 0);
        ++matches;
      }
    }
    if (matches != 1)
      throw new InvalidDataException("VISE: could not identify a unique substitution table.");
  }

  /// <summary>
  /// DATA 0 as the installer stores it: a literal table or initializer, or A89F000C-packed with the
  /// <c>CODE</c> word dictionary (CODE 24, relocated CODE 5002, CODE 23, 18 or 20; a packed
  /// dictionary takes its own from CODE 25 or 1002). Null when the fork has no DATA 0.
  /// </summary>
  public static byte[]? LoadData0(byte[] resource) {
    var packed = FindResource(resource, "DATA", 0);
    if (packed == null)
      return null;
    if (packed.Length < 4 || Be32(packed, 0) != 0xA89F000C)
      return packed;
    var code = FindResource(resource, "CODE", 24);
    if (code == null) {
      var relocated = FindResource(resource, "CODE", 5002);
      // VISE 5.5.1 can relocate the identical unpacker to CODE 5002, recognisable by A8 9F "VISE".
      ReadOnlySpan<byte> unpacker = [0xA8, 0x9F, 0x56, 0x49, 0x53, 0x45];
      if (relocated != null && relocated.Length >= 0x18 && relocated.AsSpan(0x10, 6).SequenceEqual(unpacker))
        code = relocated;
    }
    code ??= FindResource(resource, "CODE", 23) ?? FindResource(resource, "CODE", 18) ?? FindResource(resource, "CODE", 20)
      ?? throw new InvalidDataException("VISE: packed DATA 0 lacks its dictionary resource.");
    if (code.Length >= 4 && Be32(code, 0) == 0xA89F000C)
      code = FindResource(resource, "CODE", 25) ?? FindResource(resource, "CODE", 1002)
        ?? throw new InvalidDataException("VISE: compressed DATA 0 dictionary lacks its own dictionary resource.");
    return UnpackCode(packed, code);
  }

  /// <summary>The substitution permutation of an installer: from DATA 0 when it has one, else from its PEF application.</summary>
  public static byte[] Table(byte[] data, byte[]? data0) {
    var table = new byte[256];
    if (data0 == null) {
      if (!PefTable(data, table))
        throw new InvalidDataException("VISE: could not find a unique substitution table in the PEF application.");
    } else if (FindPermutation(data0, table) != 1)
      Data0Table(data0, table);
    return table;
  }
}
