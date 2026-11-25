#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Reiser4;

/// <summary>A large (four-word) Reiser4 key, compared word by word as unsigned values.</summary>
/// <remarks>
/// The first word holds the locality in its top sixty bits and the item type ("minor") in its
/// low four; then come the ordering, the object id and the offset. See
/// <c>docs/REISER4-NATIVE-PROFILE.md</c> for how each item kind fills them.
/// </remarks>
internal readonly record struct Reiser4Key(ulong Word0, ulong Ordering, ulong ObjectId, ulong Offset) : IComparable<Reiser4Key> {

  internal const byte MinorName = 0;
  internal const byte MinorStatData = 1;
  internal const byte MinorBody = 4;

  internal ulong Locality => this.Word0 >> 4;
  internal byte Minor => (byte)(this.Word0 & 0xF);

  internal static Reiser4Key Of(ulong locality, byte minor, ulong ordering, ulong objectId, ulong offset)
    => new(locality << 4 | minor, ordering, objectId, offset);

  /// <summary>The stat-data key of the object this key belongs to.</summary>
  internal Reiser4Key StatKey => Of(this.Locality, MinorStatData, this.Ordering, this.ObjectId, 0);

  public int CompareTo(Reiser4Key other) {
    var c = this.Word0.CompareTo(other.Word0);
    if (c != 0) return c;
    c = this.Ordering.CompareTo(other.Ordering);
    if (c != 0) return c;
    c = this.ObjectId.CompareTo(other.ObjectId);
    return c != 0 ? c : this.Offset.CompareTo(other.Offset);
  }

  internal static Reiser4Key Read(ReadOnlySpan<byte> at) => new(
    BinaryPrimitives.ReadUInt64LittleEndian(at),
    BinaryPrimitives.ReadUInt64LittleEndian(at[8..]),
    BinaryPrimitives.ReadUInt64LittleEndian(at[16..]),
    BinaryPrimitives.ReadUInt64LittleEndian(at[24..]));

  internal void Write(Span<byte> at) {
    BinaryPrimitives.WriteUInt64LittleEndian(at, this.Word0);
    BinaryPrimitives.WriteUInt64LittleEndian(at[8..], this.Ordering);
    BinaryPrimitives.WriteUInt64LittleEndian(at[16..], this.ObjectId);
    BinaryPrimitives.WriteUInt64LittleEndian(at[24..], this.Offset);
  }
}

/// <summary>
/// How long a volume's keys are, and everything on disk whose size follows from that: a large key
/// is four words (locality and type, ordering, object id, offset), a short key three — it has no
/// ordering word. Bit 0 of the format40 flags says which one a volume uses.
/// </summary>
/// <remarks>
/// <para>A short key is held in memory as a <see cref="Reiser4Key" /> whose ordering is zero, so
/// keys of either kind compare and sort alike. Its sizes, measured on volumes the patched
/// <c>mkfs.reiser4</c> formatted and a <c>REISER4_LARGE_KEY=0</c> kernel filled
/// (<c>docs/REISER4-NATIVE-PROFILE.md</c>, "Short keys"): a 30-byte item header, an 18-byte
/// directory unit header (object id and offset of the entry key, then the unit offset) and a
/// 16-byte entry target (the target's first word and object id).</para>
/// </remarks>
internal sealed class Reiser4KeyShape {

  /// <summary>Four-word keys: what <c>mkfs.reiser4</c> makes unless told otherwise.</summary>
  internal static readonly Reiser4KeyShape Large = new(large: true);

  /// <summary>Three-word keys, which only a kernel compiled with <c>REISER4_LARGE_KEY=0</c> mounts.</summary>
  internal static readonly Reiser4KeyShape Short = new(large: false);

  private Reiser4KeyShape(bool large) {
    this.IsLarge = large;
    this.KeyBytes = large ? 32 : 24;
  }

  /// <summary>The format40 flag bit that marks a volume of large keys.</summary>
  internal const ulong LargeKeysFlag = 1;

  internal static Reiser4KeyShape FromFormatFlags(ulong flags) => (flags & LargeKeysFlag) != 0 ? Large : Short;

  internal bool IsLarge { get; }

  /// <summary>Bytes of one key on disk.</summary>
  internal int KeyBytes { get; }

  /// <summary>An item header: the key, then the body's offset, the flags and the item plugin.</summary>
  internal int ItemHeaderBytes => this.KeyBytes + 6;

  /// <summary>The part of an entry key a directory unit header keeps: every word but the first.</summary>
  internal int EntryIdBytes => this.KeyBytes - 8;

  /// <summary>A directory unit header: the entry id, then the offset of the unit's body.</summary>
  internal int UnitHeaderBytes => this.EntryIdBytes + 2;

  /// <summary>An entry's target: its stat key without the offset.</summary>
  internal int TargetBytes => this.KeyBytes - 8;

  /// <summary>Names up to this many bytes are held in the entry key itself and stored nowhere else.</summary>
  internal int MaxInlineLength => this.IsLarge ? 23 : 15;

  internal Reiser4Key Read(ReadOnlySpan<byte> at) => this.IsLarge
    ? Reiser4Key.Read(at)
    : new(BinaryPrimitives.ReadUInt64LittleEndian(at), 0, BinaryPrimitives.ReadUInt64LittleEndian(at[8..]), BinaryPrimitives.ReadUInt64LittleEndian(at[16..]));

  internal void Write(Reiser4Key key, Span<byte> at) {
    if (this.IsLarge) {
      key.Write(at);
      return;
    }
    if (key.Ordering != 0) throw new InvalidDataException("Reiser4: a key with an ordering word cannot be written as a short key.");
    BinaryPrimitives.WriteUInt64LittleEndian(at, key.Word0);
    BinaryPrimitives.WriteUInt64LittleEndian(at[8..], key.ObjectId);
    BinaryPrimitives.WriteUInt64LittleEndian(at[16..], key.Offset);
  }

  /// <summary>The ordering, object id and offset of an entry key as a unit header holds them.</summary>
  internal (ulong Ordering, ulong ObjectId, ulong Offset) ReadEntryId(ReadOnlySpan<byte> at) => this.IsLarge
    ? (BinaryPrimitives.ReadUInt64LittleEndian(at), BinaryPrimitives.ReadUInt64LittleEndian(at[8..]), BinaryPrimitives.ReadUInt64LittleEndian(at[16..]))
    : (0, BinaryPrimitives.ReadUInt64LittleEndian(at), BinaryPrimitives.ReadUInt64LittleEndian(at[8..]));

  internal void WriteEntryId(Reiser4DirEntry entry, Span<byte> at) {
    if (this.IsLarge) {
      BinaryPrimitives.WriteUInt64LittleEndian(at, entry.Ordering);
      at = at[8..];
    } else if (entry.Ordering != 0) throw new InvalidDataException("Reiser4: an entry key with an ordering word cannot be written as a short key.");
    BinaryPrimitives.WriteUInt64LittleEndian(at, entry.ObjectId);
    BinaryPrimitives.WriteUInt64LittleEndian(at[8..], entry.Offset);
  }

  /// <summary>The stat key an entry names.</summary>
  internal Reiser4Key ReadTarget(ReadOnlySpan<byte> at) {
    var word0 = BinaryPrimitives.ReadUInt64LittleEndian(at);
    return this.IsLarge
      ? Reiser4Key.Of(word0 >> 4, Reiser4Key.MinorStatData, BinaryPrimitives.ReadUInt64LittleEndian(at[8..]), BinaryPrimitives.ReadUInt64LittleEndian(at[16..]), 0)
      : Reiser4Key.Of(word0 >> 4, Reiser4Key.MinorStatData, 0, BinaryPrimitives.ReadUInt64LittleEndian(at[8..]), 0);
  }

  internal void WriteTarget(Reiser4Key target, Span<byte> at) {
    BinaryPrimitives.WriteUInt64LittleEndian(at, target.Word0);
    if (this.IsLarge) {
      BinaryPrimitives.WriteUInt64LittleEndian(at[8..], target.Ordering);
      BinaryPrimitives.WriteUInt64LittleEndian(at[16..], target.ObjectId);
    } else {
      if (target.Ordering != 0) throw new InvalidDataException("Reiser4: a target with an ordering word cannot be written as a short key.");
      BinaryPrimitives.WriteUInt64LittleEndian(at[8..], target.ObjectId);
    }
  }

  /// <summary>Bytes one entry takes in a directory item: unit header, target, and the name when the key could not hold it.</summary>
  internal int EntryBytes(Reiser4DirEntry entry) => this.UnitHeaderBytes + this.TargetBytes + (entry.Hashed ? entry.Name.Length + 1 : 0);

  /// <summary>The key words of the entry for <paramref name="name" /> under this key length.</summary>
  internal (ulong Ordering, ulong ObjectId, ulong Offset, bool Hashed) NameKey(ReadOnlySpan<byte> name, Reiser4HashId hash, Reiser4FibrationId fibration)
    => this.IsLarge ? Reiser4NameKeys.Make(name, hash, fibration) : Reiser4NameKeys.MakeShort(name, hash, fibration);

  public override string ToString() => this.IsLarge ? "large keys" : "short keys";
}

/// <summary>Plugin identifiers of the hash and fibration plugin types (plugin type 3 and 4).</summary>
internal enum Reiser4HashId : ushort { Rupasov = 0, R5 = 1, Tea = 2, Fnv1 = 3, Degenerate = 4 }

internal enum Reiser4FibrationId : ushort { Lexicographic = 0, DotO = 1, Extension1 = 2, Extension3 = 3 }

/// <summary>
/// How a directory entry's key is made from its name: the bytes packed into the key where they
/// fit, a hash of the rest where they do not, and a "fibre" chosen by the fibration plugin in the
/// ordering's top seven bits.
/// </summary>
/// <remarks>
/// <para>Derived from the format description in <c>docs/REISER4-NATIVE-PROFILE.md</c> and checked
/// against directory entries a Reiser4 kernel wrote with every hash and fibration plugin; none of
/// the reference implementations' code was carried over.</para>
/// </remarks>
internal static class Reiser4NameKeys {

  /// <summary>Names up to this many bytes are held in the key itself and stored nowhere else.</summary>
  internal const int MaxInlineLength = 23;

  /// <summary>The ordering bit that says the name was hashed rather than held.</summary>
  internal const ulong LongNameBit = 0x0100_0000_0000_0000;

  private const int FibreShift = 57;

  /// <summary>Up to eight (or seven) bytes of <paramref name="name" />, big-endian, from <paramref name="from" />.</summary>
  private static ulong Pack(ReadOnlySpan<byte> name, int from, int skip) {
    ulong value = 0;
    var room = 8 - skip;
    var taken = 0;
    for (var i = from; i < name.Length && taken < room; ++i, ++taken)
      value = value << 8 | name[i];
    return value << (room - taken) * 8;
  }

  /// <summary>Whether <paramref name="hash" /> can key new long names this writer is asked to add.</summary>
  /// <remarks>
  /// Every hash plugin can. The Rupasov hash is keyed the way the kernel keys it, which is what
  /// mounts and resolves; <c>fsck.reiser4</c> evaluates its powers of ten unsigned and so disagrees
  /// on long hashed tails (see <see cref="Rupasov" />).
  /// </remarks>
  internal static bool CanHash(Reiser4HashId hash) => hash is Reiser4HashId.Rupasov or Reiser4HashId.R5
    or Reiser4HashId.Tea or Reiser4HashId.Fnv1 or Reiser4HashId.Degenerate;

  /// <summary>The three words of the entry key for <paramref name="name" />, and whether it was hashed.</summary>
  internal static (ulong Ordering, ulong ObjectId, ulong Offset, bool Hashed) Make(
      ReadOnlySpan<byte> name, Reiser4HashId hash, Reiser4FibrationId fibration) {
    if (name.Length == 1 && name[0] == (byte)'.') return (0, 0, 0, false);
    var ordering = Pack(name, 0, 1);
    var objectId = name.Length > 7 ? Pack(name, 7, 0) : 0UL;
    ulong offset = 0;
    var hashed = name.Length > MaxInlineLength;
    if (hashed) {
      ordering |= LongNameBit;
      offset = Hash(hash, name[15..]);
    } else if (name.Length > 15)
      offset = Pack(name, 15, 0);
    return (ordering | Fibre(fibration, name), objectId, offset, hashed);
  }

  /// <summary>Names up to this many bytes are held whole in a short entry key.</summary>
  internal const int MaxShortInlineLength = 15;

  /// <summary>
  /// The entry key words for <paramref name="name" /> on a short-key volume. There is no ordering
  /// word, so the object id takes what the ordering holds in a large key — the first seven bytes,
  /// the long-name bit and the fibre — and the offset the next eight bytes, or, past fifteen bytes,
  /// the hash of everything after the first seven.
  /// </summary>
  internal static (ulong Ordering, ulong ObjectId, ulong Offset, bool Hashed) MakeShort(
      ReadOnlySpan<byte> name, Reiser4HashId hash, Reiser4FibrationId fibration) {
    if (name.Length == 1 && name[0] == (byte)'.') return (0, 0, 0, false);
    var objectId = Pack(name, 0, 1);
    var hashed = name.Length > MaxShortInlineLength;
    ulong offset;
    if (hashed) {
      objectId |= LongNameBit;
      offset = Hash(hash, name[7..]);
    } else
      offset = name.Length > 7 ? Pack(name, 7, 0) : 0UL;
    return (0, objectId | Fibre(fibration, name), offset, hashed);
  }

  /// <summary>Whether an entry key with these words hashed its name rather than holding it.</summary>
  /// <remarks>A large entry key always has a non-zero ordering (its first name byte), so a zero
  /// ordering means either "." or a short key, whose object id carries the long-name bit.</remarks>
  internal static bool IsHashed(ulong ordering, ulong objectId) => ((ordering != 0 ? ordering : objectId) & LongNameBit) != 0;

  /// <summary>The fibre bits a name sorts under, already shifted into place.</summary>
  internal static ulong Fibre(Reiser4FibrationId fibration, ReadOnlySpan<byte> name) {
    var n = name.Length;
    ulong fibre = fibration switch {
      Reiser4FibrationId.Lexicographic => 0,
      Reiser4FibrationId.DotO => n > 2 && name[n - 1] == (byte)'o' && name[n - 2] == (byte)'.' ? 1UL : 0,
      Reiser4FibrationId.Extension1 => n > 2 && name[n - 2] == (byte)'.' ? (ulong)(sbyte)name[n - 1] : 0,
      Reiser4FibrationId.Extension3 => n > 4 && name[n - 4] == (byte)'.'
        ? (ulong)((sbyte)name[n - 3] + (sbyte)name[n - 2] + (sbyte)name[n - 1]) : 0,
      _ => throw new NotSupportedException($"Reiser4: fibration plugin {fibration} is not supported."),
    };
    // Only seven bits survive the shift: the eighth and above fall off the word.
    return fibre << FibreShift;
  }

  internal static ulong Hash(Reiser4HashId hash, ReadOnlySpan<byte> tail) => hash switch {
    Reiser4HashId.Rupasov => Rupasov(tail),
    Reiser4HashId.R5 => R5(tail),
    Reiser4HashId.Tea => Tea(tail),
    Reiser4HashId.Fnv1 => Fnv1(tail),
    Reiser4HashId.Degenerate => 0xC0C0_C0C0_1010_1010UL,
    _ => throw new NotSupportedException($"Reiser4: names cannot be hashed with the {hash} hash."),
  };

  /// <summary>
  /// The Rupasov ("Yura") hash as the kernel evaluates it: the bytes, less 48, weighted as decimal
  /// digits — byte <c>i</c> of <c>n</c> by <c>10^(n-1-i)</c> — plus every number from
  /// <c>max(n, 40)</c> to 255, the sum shifted left by seven.
  /// </summary>
  /// <remarks>
  /// Each power of ten is a 32-bit signed integer that wraps and is sign-extended into the 64-bit
  /// sum, and the leading byte's product is itself formed in 32 bits before it is widened. That is
  /// what the kernel keys entries with, checked on every long name of the kernel-written
  /// <c>rupasov</c> reference volume. reiser4progs widens the same values unsigned instead, so the
  /// two disagree as soon as a power of ten or the leading product reaches 2^31 — in practice on
  /// every hashed tail longer than nine bytes.
  /// </remarks>
  internal static ulong Rupasov(ReadOnlySpan<byte> data) {
    var n = data.Length;
    if (n == 0) return 0;
    var power = 1;
    for (var k = 1; k < n; ++k) power = unchecked(power * 10);
    var sum = (ulong)(long)unchecked((data[0] - 48) * power);
    for (var i = 1; i < n; ++i) {
      var weight = 1;
      for (var k = i; k < n - 1; ++k) weight = unchecked(weight * 10);
      sum = unchecked(sum + (ulong)(long)(data[i] - 48) * (ulong)(long)weight);
    }
    for (var i = Math.Max(n, 40); i < 256; ++i) sum += (ulong)i;
    return sum << 7;
  }

  private static ulong R5(ReadOnlySpan<byte> data) {
    ulong a = 0;
    foreach (var b in data) {
      a += (ulong)b << 4;
      a += (ulong)b >> 4;
      a *= 11;
    }
    return a;
  }

  /// <summary>FNV-1 (64-bit): multiply by the prime, then xor the octet.</summary>
  private static ulong Fnv1(ReadOnlySpan<byte> data) {
    var a = 0xCBF2_9CE4_8422_2325UL;
    foreach (var b in data) {
      a *= 0x0000_0100_0000_01B3UL;
      a ^= b;
    }
    return a;
  }

  /// <summary>
  /// The TEA-based Davies–Meyer hash in the 64-bit arithmetic Reiser4 evaluates it in: sixteen-byte
  /// blocks get six rounds, the padded remainder ten, and the result is the two halves xored.
  /// </summary>
  private static ulong Tea(ReadOnlySpan<byte> name) {
    ulong h0 = 0x9464A485u, h1 = 0x542E1A94u;
    var len = name.Length;
    var pad = (ulong)(uint)len | (ulong)(uint)len << 8;
    pad |= pad << 16;

    static ulong Word(ReadOnlySpan<byte> s) => s[0] | (ulong)s[1] << 8 | (ulong)s[2] << 16 | (ulong)s[3] << 24;

    void Mix(ulong a, ulong b, ulong c, ulong d, int rounds) {
      ulong sum = 0, b0 = h0, b1 = h1;
      for (var n = 0; n < rounds; ++n) {
        sum += 0x9E3779B9u;
        b0 += ((b1 << 4) + a) ^ (b1 + sum) ^ ((b1 >> 5) + b);
        b1 += ((b0 << 4) + c) ^ (b0 + sum) ^ ((b0 >> 5) + d);
      }
      h0 += b0;
      h1 += b1;
    }

    var at = 0;
    while (len - at >= 16) {
      var s = name[at..];
      Mix(Word(s), Word(s[4..]), Word(s[8..]), Word(s[12..]), 6);
      at += 16;
    }

    var rest = name[at..];
    ulong wa, wb, wc, wd;
    static ulong Tail(ReadOnlySpan<byte> rest, int from, ulong pad) {
      var v = pad;
      for (var i = from; i < rest.Length; ++i) v = v << 8 | rest[i];
      return v;
    }
    if (rest.Length >= 12) { wa = Word(rest); wb = Word(rest[4..]); wc = Word(rest[8..]); wd = Tail(rest, 12, pad); }
    else if (rest.Length >= 8) { wa = Word(rest); wb = Word(rest[4..]); wc = Tail(rest, 8, pad); wd = pad; }
    else if (rest.Length >= 4) { wa = Word(rest); wb = Tail(rest, 4, pad); wc = wd = pad; }
    else { wa = Tail(rest, 0, pad); wb = wc = wd = pad; }
    Mix(wa, wb, wc, wd, 10);
    return h0 ^ h1;
  }

  /// <summary>The name a key holds whole, or <see langword="null" /> when the key hashed it.</summary>
  internal static byte[]? Unpack(ulong ordering, ulong objectId, ulong offset) {
    if (IsHashed(ordering, objectId)) return null;
    if (ordering == 0 && objectId == 0 && offset == 0) return [(byte)'.'];
    // A short key: seven bytes in the object id, eight in the offset (see IsHashed).
    if (ordering == 0) (ordering, objectId, offset) = (objectId, offset, 0);
    var bytes = new List<byte>(23);
    static bool Take(List<byte> into, ulong value, int count) {
      for (var shift = (count - 1) * 8; shift >= 0; shift -= 8) {
        var b = (byte)(value >> shift);
        if (b == 0) return false;
        into.Add(b);
      }
      return true;
    }
    if (Take(bytes, ordering & 0x00FF_FFFF_FFFF_FFFFUL, 7) && Take(bytes, objectId, 8)) Take(bytes, offset, 8);
    return [.. bytes];
  }
}
