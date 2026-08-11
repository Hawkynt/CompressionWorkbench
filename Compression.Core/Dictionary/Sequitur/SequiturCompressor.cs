using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Sequitur;

/// <summary>
/// A clean-room implementation of Sequitur: an online algorithm that infers a
/// straight-line context-free grammar from a sequence in a single left-to-right
/// pass by continuously enforcing two invariants as each symbol is appended.
/// </summary>
/// <remarks>
/// <para>
/// Implemented from the published description in Nevill-Manning &amp; Witten,
/// "Identifying Hierarchical Structure in Sequences: A Linear-Time Algorithm",
/// Journal of Artificial Intelligence Research 7 (1997), 67-82 — not ported or
/// paraphrased from any third-party source code.
/// </para>
/// <para>
/// <b>Digram uniqueness.</b> No pair of adjacent symbols (a "digram") may occur
/// more than once anywhere across the grammar — the start sequence and every
/// rule body together. A single grammar-wide index maps each digram to the one
/// occurrence of it that exists. When appending a symbol, or splicing one in
/// while restoring an invariant, produces a second occurrence, the two are
/// merged: if the older occurrence is precisely some rule's entire body, both
/// are replaced by a reference to that rule; otherwise a fresh rule whose body
/// is that digram is created and substituted at both sites. Two occurrences
/// that overlap — sharing a symbol, as the two "aa" digrams in "aaa" do — are
/// not two occurrences and are left alone.
/// </para>
/// <para>
/// <b>Rule utility.</b> Every rule other than the start rule must be referenced
/// more than once. The moment a substitution drops a rule to a single
/// reference, that rule is eliminated: its body is spliced back in place of the
/// lone reference, and the two digrams newly formed at the splice boundaries
/// are themselves checked for uniqueness. This is what stops the grammar
/// filling with rules that cost more to declare than they save.
/// </para>
/// <para>
/// <b>Why it compresses.</b> Enforcing the two invariants to a fixed point
/// makes every repeated phrase collapse into a rule, and repeated
/// <i>sequences</i> of rules collapse in turn, so a sequence built from many
/// copies of one phrase ends up as a shallow hierarchy of rules plus a very
/// short start sequence, whatever the length of the repeated phrase. Input
/// with no repetition at all yields no rules, and the start sequence is then
/// the input itself.
/// </para>
/// <para>
/// <b>Wire format.</b> A four-byte little-endian original length and a varint
/// rule count, then one bit stream packed most-significant-bit first: the
/// length of each rule body as an Elias gamma code of the length minus one (so
/// the usual two-symbol body costs one bit), the start sequence length as a
/// gamma code, and then every rule body in index order followed by the start
/// sequence. A symbol is a one-bit tag and then either an eight-bit byte value
/// or a rule index at the width the rule count needs; a grammar with no rules
/// drops the tag and stores plain bytes. Zero bits pad to a byte boundary.
/// Rules are numbered by first appearance in a breadth-first walk of the
/// grammar — the start sequence left to right, then the body of rule 0, then
/// rule 1, and so on — so the numbering can be recomputed from the serialised
/// form itself and does not depend on the order in which the rules were built.
/// The grammar is serialised as-is with no follow-on entropy coding, so input
/// with no exploitable repetition ends up somewhat larger than it started:
/// Sequitur still builds rules for digrams that recur by chance, and each one
/// costs more to declare than the two symbols it saves.
/// </para>
/// </remarks>
public static class SequiturCompressor {
  /// <summary>
  /// Compresses data by inferring a Sequitur grammar and serialising its rules
  /// and start sequence.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var output = new List<byte>(64);
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.AddRange(header);

    if (data.Length == 0)
      return [.. output];

    var grammar = new Grammar();
    foreach (var b in data)
      grammar.Append(b);

    var (rules, startSequence) = grammar.Render();

    WriteVarUInt(output, (uint)rules.Count);

    var ruleBits = RuleBits(rules.Count);
    var writer = new BitWriter(output);
    foreach (var body in rules)
      writer.WriteGamma(body.Count - 1);
    writer.WriteGamma(startSequence.Count);

    foreach (var body in rules)
      foreach (var symbol in body)
        WriteSymbol(writer, symbol, rules.Count, ruleBits);
    foreach (var symbol in startSequence)
      WriteSymbol(writer, symbol, rules.Count, ruleBits);
    writer.Flush();

    return [.. output];

    static void WriteSymbol(BitWriter writer, int symbol, int ruleCount, int ruleBits) {
      if (ruleCount == 0) {
        writer.Write(symbol, 8);
        return;
      }

      if (symbol < 256) {
        writer.Write(0, 1);
        writer.Write(symbol, 8);
      } else {
        writer.Write(1, 1);
        writer.Write(symbol - 256, ruleBits);
      }
    }
  }

  /// <summary>
  /// Decompresses data previously produced by <see cref="Compress"/> by
  /// expanding the serialised grammar's start sequence.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  /// <returns>The original data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("Sequitur: truncated header.");

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize < 0)
      throw new InvalidDataException("Sequitur: negative original length.");
    if (originalSize == 0)
      return [];

    var offset = 4;
    var ruleCount = (int)ReadVarUInt(data, ref offset);

    var ruleBits = RuleBits(ruleCount);
    var reader = new BitReader(data, offset);

    var lengths = new int[ruleCount + 1];
    for (var i = 0; i < ruleCount; ++i)
      lengths[i] = reader.ReadGamma() + 1;
    lengths[ruleCount] = reader.ReadGamma();

    var rules = new int[ruleCount][];
    for (var i = 0; i < ruleCount; ++i) {
      var body = new int[lengths[i]];
      for (var k = 0; k < body.Length; ++k)
        body[k] = ReadSymbol(ref reader, ruleCount, ruleBits);
      rules[i] = body;
    }

    var startSequence = new int[lengths[ruleCount]];
    for (var k = 0; k < startSequence.Length; ++k)
      startSequence[k] = ReadSymbol(ref reader, ruleCount, ruleBits);

    static int ReadSymbol(ref BitReader reader, int ruleCount, int ruleBits) {
      if (ruleCount == 0)
        return reader.Read(8);
      return reader.Read(1) == 0 ? reader.Read(8) : 256 + reader.Read(ruleBits);
    }

    var result = new byte[originalSize];
    var resultPos = 0;

    var stack = new Stack<int>();
    for (var i = startSequence.Length - 1; i >= 0; --i)
      stack.Push(startSequence[i]);

    while (stack.Count > 0) {
      var symbol = stack.Pop();
      if (symbol < 256) {
        if (resultPos >= originalSize)
          throw new InvalidDataException("Sequitur: grammar expands past the declared length.");
        result[resultPos++] = (byte)symbol;
        continue;
      }

      var index = symbol - 256;
      if (index >= ruleCount)
        throw new InvalidDataException("Sequitur: reference to a rule that does not exist.");
      var body = rules[index];
      for (var i = body.Length - 1; i >= 0; --i)
        stack.Push(body[i]);
    }

    if (resultPos != originalSize)
      throw new InvalidDataException($"Sequitur decompressed size mismatch: expected {originalSize}, got {resultPos}.");

    return result;
  }

  /// <summary>The code width, in bits, that holds any rule index of a grammar with <paramref name="ruleCount"/> rules.</summary>
  private static int RuleBits(int ruleCount) {
    var bits = 1;
    while ((1 << bits) < ruleCount)
      ++bits;
    return bits;
  }

  /// <summary>Writes <paramref name="value"/> as a little-endian base-128 varint (7 payload bits per byte, high bit = continuation).</summary>
  private static void WriteVarUInt(List<byte> output, uint value) {
    while (value >= 0x80) {
      output.Add((byte)(value | 0x80));
      value >>= 7;
    }

    output.Add((byte)value);
  }

  /// <summary>Reads a value written by <see cref="WriteVarUInt"/>, advancing <paramref name="offset"/> past it.</summary>
  private static uint ReadVarUInt(ReadOnlySpan<byte> span, ref int offset) {
    uint result = 0;
    var shift = 0;
    byte b;
    do {
      if (offset >= span.Length)
        throw new InvalidDataException("Sequitur: truncated varint.");
      b = span[offset++];
      result |= (uint)(b & 0x7F) << shift;
      shift += 7;
    } while ((b & 0x80) != 0);

    return result;
  }

  /// <summary>Packs fixed-width codes most-significant-bit first onto the end of a byte list.</summary>
  private sealed class BitWriter(List<byte> output) {
    private int _buffer;
    private int _count;

    public void Write(int value, int bits) {
      for (var b = bits - 1; b >= 0; --b) {
        this._buffer = (this._buffer << 1) | ((value >> b) & 1);
        if (++this._count != 8)
          continue;
        output.Add((byte)this._buffer);
        this._buffer = 0;
        this._count = 0;
      }
    }

    /// <summary>
    /// Writes a positive value as an Elias gamma code: its bit length minus one
    /// in unary zeros, then the value itself. A rule body of the usual two
    /// symbols therefore costs a single bit.
    /// </summary>
    public void WriteGamma(int value) {
      var bits = 1;
      while ((1 << bits) <= value)
        ++bits;
      this.Write(0, bits - 1);
      this.Write(value, bits);
    }

    public void Flush() {
      while (this._count != 0)
        this.Write(0, 1);
    }
  }

  /// <summary>Reads the fixed-width codes written by <see cref="BitWriter"/>.</summary>
  private ref struct BitReader(ReadOnlySpan<byte> data, int offset) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = offset;
    private int _buffer;
    private int _count;

    public int Read(int bits) {
      var value = 0;
      for (var i = 0; i < bits; ++i) {
        if (this._count == 0) {
          if (this._position >= this._data.Length)
            throw new InvalidDataException("Sequitur: truncated symbol stream.");
          this._buffer = this._data[this._position++];
          this._count = 8;
        }

        --this._count;
        value = (value << 1) | ((this._buffer >> this._count) & 1);
      }

      return value;
    }

    /// <summary>Reads a value written by <see cref="BitWriter.WriteGamma"/>.</summary>
    public int ReadGamma() {
      var leadingZeros = 0;
      while (this.Read(1) == 0) {
        if (++leadingZeros > 31)
          throw new InvalidDataException("Sequitur: malformed length code.");
      }

      var value = 1;
      for (var i = 0; i < leadingZeros; ++i)
        value = (value << 1) | this.Read(1);
      return value;
    }
  }

  /// <summary>
  /// A single occurrence of a symbol — a terminal byte or a reference to a rule
  /// — inside some rule's body, linked to its neighbours.
  /// </summary>
  private sealed class Sym {
    public bool IsTerminal;
    public byte Terminal;
    public Rule? Target;
    public Sym? Prev;
    public Sym? Next;
    public Rule Owner = null!;

    /// <summary>Set once this occurrence has been unlinked for good, so that a stale reference to it is a detectable no-op rather than a walk into a broken list.</summary>
    public bool Dead;

    /// <summary>The value this occurrence contributes to a digram key: a terminal is its byte, a rule reference is 256 plus the rule's serial number.</summary>
    public long Identity => this.IsTerminal ? this.Terminal : 256 + this.Target!.Id;
  }

  /// <summary>
  /// A grammar rule. The start rule is unrestricted in length and never
  /// eliminated; every other rule is created with a two-symbol body, may grow
  /// when another rule is spliced into it, and dies as soon as it is referenced
  /// only once.
  /// </summary>
  private sealed class Rule(long id) {
    public long Id => id;
    public Sym? First;
    public Sym? Last;

    /// <summary>The occurrences that reference this rule. A non-start rule with fewer than two is eliminated on sight.</summary>
    public readonly HashSet<Sym> Referrers = new(ReferenceEqualityComparer.Instance);
    public bool Dead;
  }

  /// <summary>
  /// Builds a Sequitur grammar incrementally from appended bytes, restoring
  /// digram uniqueness and rule utility to a fixed point after every append.
  /// </summary>
  private sealed class Grammar {
    private readonly Rule _start;
    private readonly List<Rule> _rules = [];
    private readonly Dictionary<(long Left, long Right), Sym> _digrams = [];
    private readonly List<Rule> _underused = [];
    private long _nextRuleId;

    public Grammar() => this._start = this.NewRule();

    /// <summary>Appends one input byte to the start rule and restores both invariants.</summary>
    public void Append(byte value) {
      var sym = new Sym { IsTerminal = true, Terminal = value, Owner = this._start, Prev = this._start.Last };
      if (this._start.Last != null) this._start.Last.Next = sym; else this._start.First = sym;
      this._start.Last = sym;
      this.Check(sym.Prev);
    }

    /// <summary>
    /// Numbers the surviving rules by first appearance in a breadth-first walk
    /// of the finished grammar and renders it as (rule bodies, start sequence)
    /// using the codebook terminal = 0..255, non-terminal = 256 + rule index.
    /// </summary>
    /// <remarks>
    /// The walk reads the start sequence left to right, giving the next free
    /// index to each rule reference it has not seen before, then does the same
    /// over the body of rule 0, then rule 1, and so on until no rule is left
    /// unnumbered. The numbering is therefore a property of the grammar that is
    /// being written out — it can be recomputed from the serialised form alone
    /// — and owes nothing to the order in which the rules happened to be
    /// created, how many died on the way, or how any collection enumerates.
    /// </remarks>
    public (List<List<int>> Rules, List<int> StartSequence) Render() {
      var live = new List<Rule>();
      var index = new Dictionary<Rule, int>(ReferenceEqualityComparer.Instance);
      var walked = 0;

      NumberBody(this._start.First);
      Drain();

      // Every live rule of a well-formed Sequitur grammar is reachable from the
      // start sequence, so this tail never runs. It is here so that an
      // unreachable rule would still get a defined index — creation order,
      // after everything reachable — instead of being dropped and leaving the
      // bodies that mention it dangling.
      foreach (var rule in this._rules) {
        if (rule.Dead || ReferenceEquals(rule, this._start) || index.ContainsKey(rule))
          continue;
        Number(rule);
        Drain();
      }

      var rules = new List<List<int>>(live.Count);
      foreach (var rule in live) {
        var body = new List<int>();
        for (var s = rule.First; s != null; s = s.Next)
          body.Add(Code(s, index));
        rules.Add(body);
      }

      var startSequence = new List<int>();
      for (var s = this._start.First; s != null; s = s.Next)
        startSequence.Add(Code(s, index));

      return (rules, startSequence);

      static int Code(Sym sym, Dictionary<Rule, int> index) => sym.IsTerminal ? sym.Terminal : 256 + index[sym.Target!];

      // Numbers every rule referenced by a body that has itself just been
      // numbered, until the frontier is empty.
      void Drain() {
        for (; walked < live.Count; ++walked)
          NumberBody(live[walked].First);
      }

      void NumberBody(Sym? first) {
        for (var symbol = first; symbol != null; symbol = symbol.Next)
          if (!symbol.IsTerminal)
            Number(symbol.Target!);
      }

      void Number(Rule rule) {
        if (index.ContainsKey(rule))
          return;

        index[rule] = live.Count;
        live.Add(rule);
      }
    }

    private Rule NewRule() {
      var rule = new Rule(this._nextRuleId++);
      this._rules.Add(rule);
      return rule;
    }

    /// <summary>
    /// Examines the digram starting at <paramref name="left"/>. Registers it
    /// when it is the only occurrence, and merges it with the existing one
    /// otherwise. Returns whether a substitution took place, because that means
    /// <paramref name="left"/> and its successor no longer exist.
    /// </summary>
    private bool Check(Sym? left) {
      if (left == null || left.Dead || left.Next == null)
        return false;

      var key = (left.Identity, left.Next.Identity);
      if (!this._digrams.TryGetValue(key, out var found) || found.Dead || found.Next == null) {
        this._digrams[key] = left;
        return false;
      }

      // Occurrences that share a symbol are one occurrence of the digram, not two.
      if (ReferenceEquals(found, left) || ReferenceEquals(found.Next, left) || ReferenceEquals(left.Next, found))
        return false;

      this.Merge(left, found);
      return true;
    }

    /// <summary>
    /// Merges a newly created occurrence of a digram with the older one, either
    /// by reusing the rule the older occurrence already constitutes or by
    /// promoting the digram to a new rule and substituting it at both sites.
    /// </summary>
    private void Merge(Sym newOccurrence, Sym oldOccurrence) {
      Rule rule;
      if (!ReferenceEquals(oldOccurrence.Owner, this._start)
          && oldOccurrence.Prev == null
          && oldOccurrence.Next!.Next == null) {
        // The older occurrence is exactly some rule's whole body: reuse it.
        rule = oldOccurrence.Owner;
      } else {
        rule = this.NewRule();
        // Copy first, then substitute, so a rule referenced by the digram never
        // dips below two references in between and get eliminated spuriously.
        var first = Copy(oldOccurrence, rule);
        var second = Copy(oldOccurrence.Next!, rule);
        first.Next = second;
        second.Prev = first;
        rule.First = first;
        rule.Last = second;
        // The rule body is now the canonical occurrence of this digram, so the
        // substitution below must not take the index entry away with it.
        this._digrams[(first.Identity, second.Identity)] = first;
        this.Substitute(oldOccurrence, rule);
      }

      // Restoring the invariants at the older site can cascade anywhere in the
      // grammar, including over the newer site, so the newer occurrence is
      // re-validated rather than trusted.
      if (!rule.Dead && StillOccurs(newOccurrence, rule))
        this.Substitute(newOccurrence, rule);

      // A rule that ends up referenced once has to give its body back.
      if (!rule.Dead && rule.Referrers.Count < 2) {
        this._underused.Add(rule);
        this.EliminateUnderused();
      }

      static bool StillOccurs(Sym left, Rule rule) =>
        !left.Dead
        && left.Next != null
        && left.Identity == rule.First!.Identity
        && left.Next.Identity == rule.Last!.Identity
        && rule.Last == rule.First.Next;
    }

    private Sym Copy(Sym source, Rule owner) {
      var copy = new Sym {
        IsTerminal = source.IsTerminal,
        Terminal = source.Terminal,
        Target = source.Target,
        Owner = owner,
      };
      copy.Target?.Referrers.Add(copy);
      return copy;
    }

    /// <summary>
    /// Replaces the two symbols starting at <paramref name="left"/> with a
    /// single reference to <paramref name="rule"/>, then restores both
    /// invariants around the splice.
    /// </summary>
    private void Substitute(Sym left, Rule rule) {
      var right = left.Next!;
      var owner = left.Owner;
      var before = left.Prev;
      var after = right.Next;
      var beforePrev = before?.Prev;

      this.RemoveDigram(before, left);
      this.RemoveDigram(left, right);
      this.RemoveDigram(right, after);

      var reference = new Sym { IsTerminal = false, Target = rule, Owner = owner, Prev = before, Next = after };
      if (before != null) before.Next = reference; else owner.First = reference;
      if (after != null) after.Prev = reference; else owner.Last = reference;
      rule.Referrers.Add(reference);

      this.Release(left);
      this.Release(right);
      this.EliminateUnderused();

      // Both new boundaries need examining. Should the first substitute, the
      // reference is gone and the second call sees a retired symbol and stops.
      this.Check(before);
      this.Check(reference);
      this.ReleaseOverlapSuppression(beforePrev, after);
    }

    /// <summary>
    /// Re-examines the two digrams that flanked the pair just removed. Either
    /// may have been left unregistered because it overlapped a digram that has
    /// now gone from the index, which would make it the only occurrence of
    /// itself while nothing in the index says so.
    /// </summary>
    private void ReleaseOverlapSuppression(Sym? beforePrev, Sym? after) {
      this.Check(beforePrev);
      this.Check(after);
    }

    /// <summary>Retires an occurrence and, when it was a rule reference, notes any rule that has just become underused.</summary>
    private void Release(Sym sym) {
      sym.Dead = true;
      sym.Prev = null;
      sym.Next = null;
      if (sym.IsTerminal)
        return;

      var target = sym.Target!;
      target.Referrers.Remove(sym);
      if (!target.Dead && target.Referrers.Count == 1)
        this._underused.Add(target);
    }

    /// <summary>Splices the body of every rule that is down to one reference back into that reference's place.</summary>
    private void EliminateUnderused() {
      while (this._underused.Count > 0) {
        var rule = this._underused[0];
        this._underused.RemoveAt(0);
        if (rule.Dead)
          continue;
        if (rule.Referrers.Count == 0) {
          // Nothing refers to it any more, so there is nothing to splice back.
          rule.Dead = true;
          continue;
        }

        if (rule.Referrers.Count != 1)
          continue;

        Sym only = null!;
        foreach (var referrer in rule.Referrers)
          only = referrer;
        this.Expand(rule, only);
      }
    }

    /// <summary>Replaces the lone reference to <paramref name="rule"/> by the rule's own body and retires the rule.</summary>
    private void Expand(Rule rule, Sym reference) {
      var owner = reference.Owner;
      var before = reference.Prev;
      var after = reference.Next;
      var beforePrev = before?.Prev;

      this.RemoveDigram(before, reference);
      this.RemoveDigram(reference, after);

      var first = rule.First!;
      var last = rule.Last!;
      for (var s = first; s != null; s = s.Next)
        s.Owner = owner;

      first.Prev = before;
      last.Next = after;
      if (before != null) before.Next = first; else owner.First = first;
      if (after != null) after.Prev = last; else owner.Last = last;

      rule.Referrers.Remove(reference);
      rule.Dead = true;
      rule.First = null;
      rule.Last = null;
      reference.Dead = true;
      reference.Prev = null;
      reference.Next = null;

      // Only the two boundaries changed; the digrams inside the body are
      // untouched and stay in the index exactly as they were. The two are far
      // enough apart that both need examining, and a retired symbol stops the
      // second call by itself.
      this.Check(before);
      this.Check(last);
      this.ReleaseOverlapSuppression(beforePrev, after);
    }

    /// <summary>Drops the index entry for the digram (<paramref name="left"/>, <paramref name="right"/>) when that pair is the occurrence currently indexed.</summary>
    private void RemoveDigram(Sym? left, Sym? right) {
      if (left == null || right == null)
        return;

      var key = (left.Identity, right.Identity);
      if (this._digrams.TryGetValue(key, out var found) && ReferenceEquals(found, left))
        this._digrams.Remove(key);
    }
  }
}
