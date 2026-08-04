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
/// more than once anywhere across the grammar (the start sequence and every
/// rule body). The moment appending a symbol — or splicing one in as a
/// side-effect of enforcing an invariant — creates a second occurrence of some
/// digram, that digram is replaced at both occurrences by a reference to a
/// rule whose two-symbol body is that digram (reusing an existing rule for the
/// same digram if one already exists, rather than creating a duplicate).
/// </para>
/// <para>
/// <b>Rule utility.</b> Every non-start rule must be used more than once. If a
/// substitution removes one of a rule's two remaining references, that rule is
/// eliminated: its single remaining reference is replaced in place by the
/// rule's own two-symbol body ("inlining"), which in turn creates two new
/// adjacent digrams that are themselves checked for uniqueness.
/// </para>
/// <para>
/// <b>Structure.</b> A rule is created with exactly two symbols (a digram).
/// Its hierarchy normally comes from rules referencing other rules, but a
/// rule's body can grow past two symbols later: rule-utility elimination
/// splices an inlined rule's body wherever its one remaining reference sits,
/// and that site can itself be inside another rule's body, not only the
/// start sequence. Only the distinguished start rule is unrestricted in
/// length from the outset and exempt from the rule-utility check.
/// </para>
/// <para>
/// <b>Locality.</b> Because digram uniqueness is tracked in a single
/// grammar-wide index keyed by symbol identity (not by position), a new
/// digram is always checked against the <i>entire</i> grammar, not just its
/// neighbourhood — but only digrams that just changed (the append point, or
/// the boundaries created by a substitution or an inlining) are ever
/// re-examined. Digrams that were already confirmed unique, or already
/// promoted to a rule, remain valid until something adjacent to them changes;
/// this locality is what keeps the algorithm linear in the input length.
/// </para>
/// <para>
/// <b>Compression is input-shaped, not guaranteed.</b> The published
/// complexity result is that grammar size stays linear in (proportional to)
/// the input, not that it is always much smaller. A block with short internal
/// periodicity (period 1-2, e.g. a run of a byte or an alternating pair)
/// collapses into a compact doubling hierarchy and keeps improving the more
/// it repeats. A longer block with no short internal periodicity, repeated
/// many times, does not: each new repetition's leading digram matches a
/// witness now sitting inside the previous repetition's rule, eroding that
/// rule down to rebuild an equivalent one rather than simply referencing it
/// again — satisfying both invariants faithfully, but only achieving
/// near-linear (not near-constant) grammar size for that shape of input. This
/// is a property of the algorithm's strictly local, single-pass digram
/// matching, not an implementation shortfall.
/// </para>
/// </remarks>
public static class SequiturCompressor {
  /// <summary>
  /// Compresses data by inferring a Sequitur grammar and serialising its
  /// rules and start sequence in a self-describing binary form.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var grammar = new Grammar();
    foreach (var b in data)
      grammar.AppendTerminal(b);

    var (rules, startSequence) = grammar.Finalize();

    WriteVarUInt(output, (uint)rules.Count);

    // Rule-utility elimination can splice an inlined rule's body into another
    // rule's body (not just the start sequence), so a rule is not always
    // exactly two symbols after the grammar settles — each rule is therefore
    // serialised the same length-prefixed way as the start sequence.
    foreach (var body in rules)
      WriteSequence(output, body);

    WriteSequence(output, startSequence);

    return output.ToArray();

    static void WriteSequence(Stream stream, List<int> sequence) {
      WriteVarUInt(stream, (uint)sequence.Count);
      foreach (var symbol in sequence)
        WriteVarUInt(stream, (uint)symbol);
    }
  }

  /// <summary>
  /// Decompresses data previously produced by <see cref="Compress"/> by
  /// expanding the serialised grammar's start sequence.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  /// <returns>The original data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var offset = 4;

    var ruleCount = (int)ReadVarUInt(data, ref offset);

    var rules = new int[ruleCount][];
    for (var i = 0; i < ruleCount; ++i)
      rules[i] = ReadSequence(data, ref offset);

    var startSequence = ReadSequence(data, ref offset);

    var result = new byte[originalSize];
    var resultPos = 0;

    var stack = new Stack<int>();
    for (var i = startSequence.Length - 1; i >= 0; --i)
      stack.Push(startSequence[i]);

    while (stack.Count > 0) {
      var symbol = stack.Pop();
      if (symbol < 256) {
        result[resultPos++] = (byte)symbol;
      } else {
        var body = rules[symbol - 256];
        for (var i = body.Length - 1; i >= 0; --i)
          stack.Push(body[i]);
      }
    }

    if (resultPos != originalSize)
      throw new InvalidDataException($"Sequitur decompressed size mismatch: expected {originalSize}, got {resultPos}.");

    return result;

    static int[] ReadSequence(ReadOnlySpan<byte> span, ref int offset) {
      var length = (int)ReadVarUInt(span, ref offset);
      var sequence = new int[length];
      for (var i = 0; i < length; ++i)
        sequence[i] = (int)ReadVarUInt(span, ref offset);
      return sequence;
    }
  }

  /// <summary>Writes <paramref name="value"/> as a little-endian base-128 varint (7 payload bits per byte, high bit = continuation).</summary>
  private static void WriteVarUInt(Stream stream, uint value) {
    while (value >= 0x80) {
      stream.WriteByte((byte)(value | 0x80));
      value >>= 7;
    }
    stream.WriteByte((byte)value);
  }

  /// <summary>Reads a value written by <see cref="WriteVarUInt"/>, advancing <paramref name="offset"/> past it.</summary>
  private static uint ReadVarUInt(ReadOnlySpan<byte> span, ref int offset) {
    uint result = 0;
    var shift = 0;
    byte b;
    do {
      b = span[offset++];
      result |= (uint)(b & 0x7F) << shift;
      shift += 7;
    } while ((b & 0x80) != 0);
    return result;
  }

  /// <summary>
  /// A single occurrence of a symbol (terminal byte or non-terminal rule
  /// reference) inside some rule's body, linked to its neighbours.
  /// </summary>
  private sealed class Sym {
    public bool IsTerminal;
    public byte Terminal;
    public Rule? Target;
    public Sym? Prev;
    public Sym? Next;
    public Rule Owner = null!;

    /// <summary>
    /// Set once this occurrence has been permanently removed from every rule
    /// body, so that stale entries left on the pending-check worklist become
    /// safe no-ops instead of reading invalidated neighbour pointers.
    /// </summary>
    public bool Dead;
  }

  /// <summary>
  /// A grammar rule: the distinguished start rule (arbitrary length, never
  /// eliminated) or a digram rule (created with exactly two symbols, later
  /// possibly grown by inlining another rule into it, and itself eliminated
  /// by inlining if its own reference count ever drops to one).
  /// </summary>
  private sealed class Rule {
    public Sym? First;
    public Sym? Last;
    public readonly HashSet<Sym> Referrers = new(ReferenceEqualityComparer.Instance);
    public bool IsDeleted;
  }

  private readonly record struct SymIdentity(bool IsTerminal, byte Terminal, Rule? Target);
  private readonly record struct DigramKey(SymIdentity A, SymIdentity B);

  /// <summary>
  /// Builds a Sequitur grammar incrementally from appended terminal symbols,
  /// maintaining digram uniqueness and rule utility after every append.
  /// </summary>
  private sealed class Grammar {
    private readonly Rule _start = new();
    private readonly Dictionary<DigramKey, Sym> _witness = [];
    private readonly Dictionary<DigramKey, Rule> _ruleByDigram = [];
    private readonly Stack<Sym> _pending = new();

    /// <summary>Appends a terminal byte to the start rule and restores both invariants.</summary>
    public void AppendTerminal(byte value) {
      var sym = new Sym { IsTerminal = true, Terminal = value };
      this.AppendToTail(this._start, sym);
      this.Enforce(sym.Prev);
    }

    /// <summary>
    /// Assigns dense indices to the surviving rules and renders the grammar
    /// as (rule bodies, start sequence) using the codebook terminal=0..255,
    /// non-terminal=256+ruleIndex.
    /// </summary>
    /// <remarks>
    /// A rule is created with exactly two symbols, but rule-utility
    /// elimination can later splice an inlined rule's body into another
    /// rule's body (not only into the start sequence), so a rule's body can
    /// grow beyond two symbols by the time the grammar settles. Every body
    /// is therefore rendered as a full symbol list, not just its two ends.
    /// </remarks>
    public (List<List<int>> Rules, List<int> StartSequence) Finalize() {
      var liveRules = new List<Rule>(this._ruleByDigram.Values);
      var index = new Dictionary<Rule, int>(ReferenceEqualityComparer.Instance);
      for (var i = 0; i < liveRules.Count; ++i)
        index[liveRules[i]] = i;

      var rules = new List<List<int>>(liveRules.Count);
      foreach (var rule in liveRules) {
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
    }

    private void AppendToTail(Rule rule, Sym sym) {
      sym.Owner = rule;
      sym.Prev = rule.Last;
      sym.Next = null;
      if (rule.Last != null) rule.Last.Next = sym; else rule.First = sym;
      rule.Last = sym;
    }

    private static void InsertBetween(Rule owner, Sym? prev, Sym sym, Sym? next) {
      sym.Owner = owner;
      sym.Prev = prev;
      sym.Next = next;
      if (prev != null) prev.Next = sym; else owner.First = sym;
      if (next != null) next.Prev = sym; else owner.Last = sym;
    }

    /// <summary>Removes the contiguous pair (a, a.Next==b) from its owner's body, patching the surrounding links.</summary>
    private (Sym? Prev, Sym? Next) DetachPair(Sym a, Sym b) {
      var owner = a.Owner;
      var prev = a.Prev;
      var next = b.Next;

      this.RemoveWitnessIfMatches(prev, a);
      this.RemoveWitnessIfMatches(b, next);

      if (prev != null) prev.Next = next; else owner.First = next;
      if (next != null) next.Prev = prev; else owner.Last = prev;

      return (prev, next);
    }

    private void RemoveWitnessIfMatches(Sym? first, Sym? second) {
      if (first == null || second == null) return;
      var key = KeyOf(first, second);
      if (this._witness.TryGetValue(key, out var w) && ReferenceEquals(w, first))
        this._witness.Remove(key);
    }

    private static DigramKey KeyOf(Sym a, Sym b) => new(Identity(a), Identity(b));
    private static SymIdentity Identity(Sym s) => s.IsTerminal ? new SymIdentity(true, s.Terminal, null) : new SymIdentity(false, 0, s.Target);

    private void PushBoundaryChecks(Sym? prev, Sym sym) {
      if (prev != null) this._pending.Push(prev);
      this._pending.Push(sym);
    }

    /// <summary>Drains the pending worklist, restoring both invariants to a fixed point.</summary>
    private void Enforce(Sym? seed) {
      if (seed != null) this._pending.Push(seed);

      while (this._pending.Count > 0) {
        var a = this._pending.Pop();
        if (a.Dead || a.Next == null) continue;

        var b = a.Next;
        var key = KeyOf(a, b);

        if (this._ruleByDigram.TryGetValue(key, out var rule) && !rule.IsDeleted) {
          // A digram occurrence that IS a rule's own body definition needs no action.
          if (ReferenceEquals(a, rule.First) && ReferenceEquals(b, rule.Last))
            continue;
          this.ReuseRule(rule, a, b);
          continue;
        }

        if (this._witness.TryGetValue(key, out var w)) {
          if (ReferenceEquals(w, a))
            continue;

          var wb = w.Next!;
          if (ReferenceEquals(wb, a)) {
            // The two occurrences share their middle symbol (a run of equal-valued
            // symbols, e.g. "aaa"): only one physical pair exists here to promote.
            this.PromoteOverlap(key, w, wb, b);
            continue;
          }
          if (ReferenceEquals(b, w)) {
            this.PromoteOverlap(key, a, w, wb);
            continue;
          }
          if (ReferenceEquals(wb.Next, a)) {
            // The pairs are distinct but touch with no symbol between them
            // ("...,w,wb,a,b,..."): both anchors must be resolved from a single
            // coordinated splice, or detaching one pair invalidates the other's
            // captured neighbour.
            this.PromoteAdjacent(key, w, wb, a, b);
            continue;
          }
          if (ReferenceEquals(b.Next, w)) {
            this.PromoteAdjacent(key, a, b, w, wb);
            continue;
          }

          this.PromoteSeparate(key, w, wb, a, b);
          continue;
        }

        this._witness[key] = a;
      }
    }

    private void ReuseRule(Rule rule, Sym a, Sym b) {
      var owner = a.Owner;
      var (prev, next) = this.DetachPair(a, b);

      var refSym = new Sym { IsTerminal = false, Target = rule };
      InsertBetween(owner, prev, refSym, next);
      rule.Referrers.Add(refSym);
      this.PushBoundaryChecks(prev, refSym);

      a.Dead = true;
      b.Dead = true;
      this.DiscardIfNonTerminal(a);
      this.DiscardIfNonTerminal(b);
    }

    /// <summary>
    /// Promotes a repeated digram to a new rule when the witness occurrence
    /// (w, wb) and the current occurrence (a, b) are entirely independent
    /// sites — no shared symbol and no touching boundary — so each can be
    /// detached and patched without disturbing the other's anchors.
    /// </summary>
    private void PromoteSeparate(DigramKey key, Sym w, Sym wb, Sym a, Sym b) {
      var ownerA = a.Owner;
      var ownerW = w.Owner;

      var (prevA, nextA) = this.DetachPair(a, b);
      var (prevW, nextW) = this.DetachPair(w, wb);

      var rule = this.NewRuleFrom(key, w, wb);

      var refAtW = new Sym { IsTerminal = false, Target = rule };
      InsertBetween(ownerW, prevW, refAtW, nextW);
      rule.Referrers.Add(refAtW);

      var refAtA = new Sym { IsTerminal = false, Target = rule };
      InsertBetween(ownerA, prevA, refAtA, nextA);
      rule.Referrers.Add(refAtA);

      this.PushBoundaryChecks(prevW, refAtW);
      this.PushBoundaryChecks(prevA, refAtA);

      a.Dead = true;
      b.Dead = true;
      this.DiscardIfNonTerminal(a);
      this.DiscardIfNonTerminal(b);
    }

    /// <summary>
    /// Promotes a repeated digram when the witness pair (wLeft, wRight) is
    /// immediately followed by the current pair (aLeft, aRight) with no
    /// symbol between them. Detaching each pair independently would corrupt
    /// the other's captured boundary, so both are removed and replaced by a
    /// single coordinated splice instead.
    /// </summary>
    private void PromoteAdjacent(DigramKey key, Sym wLeft, Sym wRight, Sym aLeft, Sym aRight) {
      var owner = wLeft.Owner;
      var outerPrev = wLeft.Prev;
      var outerNext = aRight.Next;

      this.RemoveWitnessIfMatches(outerPrev, wLeft);
      this.RemoveWitnessIfMatches(wRight, aLeft);
      this.RemoveWitnessIfMatches(aRight, outerNext);

      var rule = this.NewRuleFrom(key, wLeft, wRight);

      var refW = new Sym { IsTerminal = false, Target = rule, Owner = owner, Prev = outerPrev };
      var refA = new Sym { IsTerminal = false, Target = rule, Owner = owner, Next = outerNext };
      refW.Next = refA;
      refA.Prev = refW;

      if (outerPrev != null) outerPrev.Next = refW; else owner.First = refW;
      if (outerNext != null) outerNext.Prev = refA; else owner.Last = refA;

      rule.Referrers.Add(refW);
      rule.Referrers.Add(refA);

      if (outerPrev != null) this._pending.Push(outerPrev);
      this._pending.Push(refW); // checks (refW, refA)
      this._pending.Push(refA); // checks (refA, outerNext)

      aLeft.Dead = true;
      aRight.Dead = true;
      this.DiscardIfNonTerminal(aLeft);
      this.DiscardIfNonTerminal(aRight);
    }

    /// <summary>
    /// Promotes a repeated digram when the witness pair (left, shared) and
    /// the current pair (shared, right) overlap at the shared middle symbol
    /// (e.g. a run of identical symbols such as "aaa"). Only one physical
    /// pair exists to remove here; the shared symbol is consumed into the
    /// new rule and <paramref name="right"/> simply becomes the following
    /// symbol.
    /// </summary>
    private void PromoteOverlap(DigramKey key, Sym left, Sym shared, Sym right) {
      var owner = left.Owner;
      var outerPrev = left.Prev;

      this.RemoveWitnessIfMatches(outerPrev, left);

      var rule = this.NewRuleFrom(key, left, shared);

      var refSym = new Sym { IsTerminal = false, Target = rule, Owner = owner, Prev = outerPrev, Next = right };
      if (outerPrev != null) outerPrev.Next = refSym; else owner.First = refSym;
      right.Prev = refSym;

      rule.Referrers.Add(refSym);

      if (outerPrev != null) this._pending.Push(outerPrev);
      this._pending.Push(refSym); // checks (refSym, right)
    }

    private Rule NewRuleFrom(DigramKey key, Sym first, Sym last) {
      var rule = new Rule { First = first, Last = last };
      first.Owner = rule;
      first.Prev = null;
      last.Owner = rule;
      last.Next = null;
      this._ruleByDigram[key] = rule;
      return rule;
    }

    private void DiscardIfNonTerminal(Sym sym) {
      if (sym.IsTerminal)
        return;

      var target = sym.Target!;
      target.Referrers.Remove(sym);
      if (!target.IsDeleted && target.Referrers.Count == 1)
        this.InlineRule(target);
    }

    /// <summary>Eliminates a rule whose reference count dropped to one, splicing its two-symbol body into the remaining reference site.</summary>
    private void InlineRule(Rule rule) {
      rule.IsDeleted = true;
      var key = KeyOf(rule.First!, rule.Last!);
      this._ruleByDigram.Remove(key);

      var onlyRef = rule.Referrers.Single();
      rule.Referrers.Clear();

      var owner = onlyRef.Owner;
      var prev = onlyRef.Prev;
      var next = onlyRef.Next;

      this.RemoveWitnessIfMatches(prev, onlyRef);
      this.RemoveWitnessIfMatches(onlyRef, next);
      onlyRef.Dead = true;

      var first = rule.First!;
      var last = rule.Last!;
      // A rule's body can have grown past two symbols via an earlier inlining
      // of its own, so every node in the body — not just the two ends — must
      // be re-owned by the splice target.
      for (var s = first; s != null; s = s.Next)
        s.Owner = owner;
      first.Prev = prev;
      last.Next = next;

      if (prev != null) prev.Next = first; else owner.First = first;
      if (next != null) next.Prev = last; else owner.Last = last;

      if (prev != null) this._pending.Push(prev);
      this._pending.Push(first); // re-checks the reconstituted (first, last) digram
      this._pending.Push(last);  // checks (last, next)
    }
  }
}
