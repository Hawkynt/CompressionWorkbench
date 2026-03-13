using System.Runtime.InteropServices;

namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Base class for PPMd (Prediction by Partial Matching) context models.
/// Provides the core context-tree machinery shared between Model H and Model I variants.
/// The model maintains a trie of byte contexts and predicts the next symbol based on
/// the longest matching context, falling back to shorter contexts via escape coding.
/// </summary>
public abstract class PpmdModelBase {
  /// <summary>Maximum context order for this model instance.</summary>
  protected readonly int _maxOrder;

  /// <summary>
  /// Context tree stored as a dictionary keyed by a context identifier.
  /// The key is a structural representation of the context byte sequence.
  /// </summary>
  private readonly Dictionary<ContextKey, PpmdContext> _contexts = new();

  /// <summary>Ring buffer of recently processed symbols for context lookup.</summary>
  private readonly byte[] _history;

  /// <summary>Current position in the history ring buffer.</summary>
  private int _historyPos;

  /// <summary>Number of symbols processed so far (capped at history size).</summary>
  private int _historyCount;

  /// <summary>
  /// Initializes the base PPMd model.
  /// </summary>
  /// <param name="maxOrder">Maximum context order (1..16).</param>
  protected PpmdModelBase(int maxOrder) {
    if (maxOrder is < 1 or > PpmdConstants.MaxOrder)
      throw new ArgumentOutOfRangeException(nameof(maxOrder), $"Order must be between 1 and {PpmdConstants.MaxOrder}.");

    this._maxOrder = maxOrder;
    // History buffer needs to hold at least maxOrder bytes
    this._history = new byte[Math.Max(maxOrder + 1, 1024)];
  }

  /// <summary>
  /// Gets the context node for the given order, using the current history.
  /// Returns <c>null</c> if no such context exists.
  /// </summary>
  /// <param name="order">The context order (0 = no context, 1 = last byte, etc.).</param>
  /// <returns>The context node, or null if it does not exist.</returns>
  protected PpmdContext? GetContext(int order) {
    if (order == 0) {
      // Order-0 context: always exists
      var key = new ContextKey(0, 0);
      if (this._contexts.TryGetValue(key, out var ctx))
        return ctx;

      ctx = new();
      this._contexts[key] = ctx;
      return ctx;
    }

    if (order > this._historyCount)
      return null;

    var contextKey = this.BuildContextKey(order);
    this._contexts.TryGetValue(contextKey, out var context);
    return context;
  }

  /// <summary>
  /// Gets or creates the context node for the given order using the current history.
  /// </summary>
  /// <param name="order">The context order.</param>
  /// <returns>The context node (created if necessary), or null if insufficient history.</returns>
  protected PpmdContext? GetOrCreateContext(int order) {
    if (order == 0) {
      var key = new ContextKey(0, 0);
      if (this._contexts.TryGetValue(key, out var ctx))
        return ctx;

      ctx = new();
      this._contexts[key] = ctx;
      return ctx;
    }

    if (order > this._historyCount)
      return null;

    var contextKey = this.BuildContextKey(order);
    if (this._contexts.TryGetValue(contextKey, out var context))
      return context;

    context = new();
    this._contexts[contextKey] = context;
    return context;
  }

  /// <summary>
  /// Updates all matching context nodes with the new symbol, then adds the symbol
  /// to the history ring buffer. Contexts are updated BEFORE the symbol is pushed
  /// so that context keys reflect the preceding byte sequence (not the current symbol).
  /// </summary>
  /// <param name="symbol">The symbol to add.</param>
  protected void UpdateModel(byte symbol) {
    // Update contexts BEFORE pushing the symbol onto the history.
    // This ensures context keys represent the preceding context, not the symbol itself.
    var maxCtxOrder = Math.Min(this._maxOrder, this._historyCount);
    for (var order = 0; order <= maxCtxOrder; ++order) {
      var ctx = this.GetOrCreateContext(order);
      if (ctx == null)
        continue;

      ctx.IncrementFreq(symbol);
      this.MaybeRescale(ctx);
    }

    // Now push the symbol onto the history
    this._history[this._historyPos] = symbol;
    this._historyPos = (this._historyPos + 1) % this._history.Length;
    if (this._historyCount < this._history.Length)
      ++this._historyCount;
  }

  /// <summary>
  /// Encodes a single byte symbol using the PPMd algorithm.
  /// Tries the highest-order context first, falling back to lower orders on escape.
  /// </summary>
  /// <param name="encoder">The range encoder.</param>
  /// <param name="symbol">The byte to encode.</param>
  public void EncodeSymbol(PpmdRangeEncoder encoder, byte symbol) {
    HashSet<byte>? excluded = null;
    var maxCtxOrder = Math.Min(this._maxOrder, this._historyCount);

    for (var order = maxCtxOrder; order >= 0; --order) {
      var ctx = this.GetContext(order);
      if (ctx == null || ctx.SymbolCount == 0)
        continue;

      var table = ctx.BuildCodingTable(excluded);
      var totalFreq = 0u;
      foreach (var entry in table)
        totalFreq += entry.Freq;

      if (totalFreq == 0)
        continue;

      // Look for the symbol in the table
      foreach (var entry in table)
        if (entry.Symbol == symbol) {
          encoder.Encode(entry.CumFreq, entry.Freq, totalFreq);
          this.UpdateModel(symbol);
          return;
        }

      // Symbol not found — encode escape
      var escapeEntry = table[^1]; // last entry is escape
      if (escapeEntry.Symbol != -1)
        // No escape entry — shouldn't happen, but handle gracefully
        continue;

      encoder.Encode(escapeEntry.CumFreq, escapeEntry.Freq, totalFreq);

      // Add all symbols from this context to the exclusion set
      excluded ??= [];
      foreach (var entry in table)
        if (entry.Symbol >= 0)
          excluded.Add((byte)entry.Symbol);
    }

    // Order -1: encode with flat distribution over all non-excluded symbols
    EncodeOrderMinus1(encoder, symbol, excluded);
    this.UpdateModel(symbol);
  }

  /// <summary>
  /// Decodes a single byte symbol using the PPMd algorithm.
  /// Tries the highest-order context first, falling back to lower orders on escape.
  /// </summary>
  /// <param name="decoder">The range decoder.</param>
  /// <returns>The decoded byte.</returns>
  public byte DecodeSymbol(PpmdRangeDecoder decoder) {
    HashSet<byte>? excluded = null;
    var maxCtxOrder = Math.Min(this._maxOrder, this._historyCount);

    for (var order = maxCtxOrder; order >= 0; --order) {
      var ctx = this.GetContext(order);
      if (ctx == null || ctx.SymbolCount == 0)
        continue;

      var table = ctx.BuildCodingTable(excluded);
      var totalFreq = 0u;
      foreach (var entry in table)
        totalFreq += entry.Freq;

      if (totalFreq == 0)
        continue;

      var threshold = decoder.GetThreshold(totalFreq);

      // Find the symbol corresponding to this threshold
      var cumFreq = 0u;
      foreach (var entry in table) {
        if (threshold < cumFreq + entry.Freq) {
          decoder.Decode(entry.CumFreq, entry.Freq, totalFreq);

          if (entry.Symbol == -1) {
            // Escape — fall to lower order
            excluded ??= [];
            foreach (var e in table)
              if (e.Symbol >= 0)
                excluded.Add((byte)e.Symbol);

            break;
          }

          var symbol = (byte)entry.Symbol;
          this.UpdateModel(symbol);
          return symbol;
        }

        cumFreq += entry.Freq;
      }
    }

    // Order -1: flat distribution
    var decoded = DecodeOrderMinus1(decoder, excluded);
    this.UpdateModel(decoded);
    return decoded;
  }

  /// <summary>
  /// Resets the model to its initial state (empty context tree, empty history).
  /// </summary>
  public void Reset() {
    this._contexts.Clear();
    this._historyPos = 0;
    this._historyCount = 0;
    this._history.AsSpan().Clear();
  }

  /// <summary>
  /// When overridden in a derived class, applies variant-specific rescaling logic.
  /// Called after a context is updated. The default implementation rescales when
  /// the total frequency exceeds <see cref="GetRescaleThreshold"/>.
  /// </summary>
  /// <param name="ctx">The context to check and possibly rescale.</param>
  protected virtual void MaybeRescale(PpmdContext ctx) {
    if (ctx.TotalFreq > this.GetRescaleThreshold())
      ctx.Rescale();
  }

  /// <summary>
  /// Gets the total frequency threshold at which a context should be rescaled.
  /// </summary>
  /// <returns>The rescale threshold.</returns>
  protected abstract int GetRescaleThreshold();

  /// <summary>
  /// Builds a context key for the given order from the current history.
  /// </summary>
  private ContextKey BuildContextKey(int order) {
    // Compute a deterministic hash of the last 'order' bytes in history
    // Use FNV-1a style hashing for good distribution
    var hash = 14695981039346656037UL;
    for (var i = order; i >= 1; --i) {
      var idx = ((this._historyPos - i) % this._history.Length + this._history.Length) % this._history.Length;
      hash ^= this._history[idx];
      hash *= 1099511628211UL;
    }

    return new(order, hash);
  }

  /// <summary>
  /// Encodes a symbol at order -1 (uniform distribution over non-excluded symbols).
  /// </summary>
  private static void EncodeOrderMinus1(PpmdRangeEncoder encoder, byte symbol, HashSet<byte>? excluded) {
    // Build a list of available symbols
    var available = 0;
    var cumFreq = 0u;
    var found = false;
    var foundCumFreq = 0u;

    for (var s = 0; s < PpmdConstants.NumSymbols; ++s) {
      var symbolByte = (byte)s;
      if (excluded != null && excluded.Contains(symbolByte))
        continue;

      if (symbolByte == symbol) {
        foundCumFreq = cumFreq;
        found = true;
      }

      ++cumFreq;
      ++available;
    }

    if (!found || available == 0) {
      // Should not happen in normal operation — encode symbol 0 with freq 1/256
      encoder.Encode(symbol, 1, PpmdConstants.NumSymbols);
      return;
    }

    encoder.Encode(foundCumFreq, 1, (uint)available);
  }

  /// <summary>
  /// Decodes a symbol at order -1 (uniform distribution over non-excluded symbols).
  /// </summary>
  private static byte DecodeOrderMinus1(PpmdRangeDecoder decoder, HashSet<byte>? excluded) {
    var available = 0;
    for (var s = 0; s < PpmdConstants.NumSymbols; ++s) {
      if (excluded != null && excluded.Contains((byte)s))
        continue;

      ++available;
    }

    if (available == 0)
      available = PpmdConstants.NumSymbols; // fallback

    var threshold = decoder.GetThreshold((uint)available);

    // Map threshold back to the original symbol
    var cumFreq = 0u;
    for (var s = 0; s < PpmdConstants.NumSymbols; ++s) {
      var symbolByte = (byte)s;
      if (excluded != null && excluded.Contains(symbolByte))
        continue;

      if (threshold == cumFreq) {
        decoder.Decode(cumFreq, 1, (uint)available);
        return symbolByte;
      }

      ++cumFreq;
    }

    // Fallback: use the last available symbol
    // This handles the edge case where threshold equals cumFreq - 1
    cumFreq = 0;
    var lastSymbol = (byte)0;
    for (var s = 0; s < PpmdConstants.NumSymbols; ++s) {
      var symbolByte = (byte)s;
      if (excluded != null && excluded.Contains(symbolByte))
        continue;

      if (threshold < cumFreq + 1) {
        decoder.Decode(cumFreq, 1, (uint)available);
        return symbolByte;
      }

      lastSymbol = symbolByte;
      ++cumFreq;
    }

    decoder.Decode(cumFreq - 1, 1, (uint)available);
    return lastSymbol;
  }

  /// <summary>
  /// A compact, hashable key identifying a context by its order and content hash.
  /// </summary>
  [StructLayout(LayoutKind.Auto)]
  protected readonly record struct ContextKey(int Order, ulong Hash);
}
