namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the ALZ stream (docs/UHARC-ON-DISK.md, "ALZ" and "Writing"): it runs the
/// decoder's <see cref="UharcAlzModel{TCoder}"/> step for step and answers each step with a
/// greedy choice — a copy at the last distance, one of the older distances or a new one found
/// by a hash chain, or a literal. It uses selector 0 (no multimedia predictor).
/// </summary>
internal sealed class UharcAlzEncoder {

  private const int MaxLength = 255;
  private const int HashBits = 16;
  private const int ChainDepth = 48;

  private readonly byte[] _data;
  private readonly int[] _head = new int[1 << HashBits];
  private readonly int[] _previous;
  private readonly uint _mask;
  private int _inserted;

  private UharcAlzEncoder(byte[] data, uint dictionarySize) {
    this._data = data;
    this._previous = new int[dictionarySize];
    this._mask = dictionarySize - 1;
    Array.Fill(this._head, -1);
  }

  /// <summary>Packs <paramref name="data"/> as ALZ stream <paramref name="method"/> (40..87).</summary>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new UharcRangeEncoder();
    var model = new UharcAlzModel<UharcEncoding>(new(coder), method, selector: 0);
    var encoder = new UharcAlzEncoder(data, model.DictionarySize);
    var resets = new UharcModelResets(fileEnds, data.Length, UharcModelResets.AlzSpan, mergeSmallFiles: false);
    var output = new byte[data.Length];
    var position = 0;
    while (position < data.Length) {
      if (resets.IsResetPoint(position))
        model.ResetForNextFile(selector: 0);

      var limit = (int)Math.Min(data.Length - position, position < resets.Next ? resets.Next - position : data.Length - position);
      var choice = encoder.Choose(model, position, limit);
      var next = model.Step(output, position, choice);
      encoder.Insert(next);
      position = next;
    }

    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: ALZ encoder lost track of its model.");
    return coder.Finish();
  }

  private int Hash(int position) => ((this._data[position] << 8 ^ this._data[position + 1] << 4 ^ this._data[position + 2]) * 0x9E37) >> 4 & ((1 << HashBits) - 1);

  /// <summary>Enters every position below <paramref name="end"/> into the hash chains.</summary>
  private void Insert(int end) {
    var last = Math.Min(end, this._data.Length - 2);
    for (; this._inserted < last; ++this._inserted) {
      var h = this.Hash(this._inserted);
      this._previous[this._inserted & this._mask] = this._head[h];
      this._head[h] = this._inserted;
    }
  }

  private int MatchLength(int position, uint distance, int limit) {
    var data = this._data;
    var source = position - (int)distance;
    var length = 0;
    var cap = Math.Min(limit, MaxLength);
    while (length < cap && data[source + length] == data[position + length])
      ++length;
    return length;
  }

  /// <summary>
  /// A copy is only taken whole: its length is the full match, or 255 for a run that goes on.
  /// A shortened copy would leave the next byte equal to the byte the decoder excludes.
  /// </summary>
  private int UsableLength(int position, uint distance, int limit) {
    var length = this.MatchLength(position, distance, limit);
    if (length == MaxLength)
      return length;
    if (length == limit && limit < this._data.Length - position) {
      // Cut by a reset point: only usable when the match really ends there.
      var source = position - (int)distance;
      if (this._data[source + length] == this._data[position + length])
        return 0;
    }

    return length;
  }

  private UharcAlzChoice Choose(UharcAlzModel<UharcEncoding> model, int position, int limit) {
    // UHARC itself reaches back at most the window less one longest copy; both 0.6b extractors
    // mis-copy distances close to the full window.
    var reach = model.DictionarySize - MaxLength;
    var literal = new UharcAlzChoice(4, 0, 0, 0, this._data[position]);
    if (position == 0)
      return literal;

    var best = literal;
    var bestScore = 0.0;

    void Consider(UharcAlzChoice choice, double score) {
      if (score > bestScore) {
        best = choice;
        bestScore = score;
      }
    }

    for (var slot = 0; slot < 12; ++slot) {
      var distance = model.Recent(slot);
      if (distance == 0 || distance > (uint)position || distance > reach)
        continue;
      var length = this.UsableLength(position, distance, limit);
      if (length >= 2)
        Consider(new(slot == 0 ? 2 : 3, 0, slot, length, 0), length - (slot == 0 ? 0.6 : 1.4));
    }

    if (position + 2 < this._data.Length) {
      var candidate = this._head[this.Hash(position)];
      for (var depth = 0; depth < ChainDepth && candidate >= 0; ++depth) {
        var distance = (uint)(position - candidate);
        if (distance > reach)
          break;
        if (CanUse(distance, position)) {
          var length = this.UsableLength(position, distance, limit);
          if (length >= UharcAlzModel<UharcEncoding>.MinimumLength(distance))
            Consider(new(1, distance, 0, length, 0), length - 2.0 - Math.Log2(distance) / 6);
        }

        var next = this._previous[candidate & this._mask];
        if (next >= candidate)
          break;
        candidate = next;
      }
    }

    if (best.Token == 4 && this._data[position] == model.Excluded)
      throw new InvalidOperationException("UHARC: ALZ encoder reached a byte it cannot code.");
    return best;
  }

  private static bool CanUse(uint distance, int position) => UharcAlzModel<UharcEncoding>.CanCodeDistance(distance, position);
}
