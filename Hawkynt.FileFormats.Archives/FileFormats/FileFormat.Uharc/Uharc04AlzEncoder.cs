namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the 0.4 ALZ stream (docs/UHARC-ON-DISK.md, "UHARC 0.4 ALZ" and "Writing
/// UHARC 0.4"): it runs the decoder's <see cref="Uharc04AlzModel{TCoder}"/> step for step and
/// answers each step greedily with a whole copy (a recent distance or one from the hash chain) or
/// a literal, with group 0 (no multimedia predictor). A copy whose length the model would take
/// from a fully trusted per-byte-pair prediction is used only when that prediction is its length.
/// </summary>
internal static class Uharc04AlzEncoder {

  /// <summary>Packs <paramref name="data"/> as 0.4 ALZ stream <paramref name="method"/> (40..87).</summary>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new UharcRangeEncoder();
    var model = new Uharc04AlzModel<UharcEncoding>(new(coder), method, fileEnds, data.Length);
    var finder = new UharcMatchFinder(data, model.DictionarySize);
    var output = new byte[data.Length];
    var position = 0;
    while (position < data.Length) {
      model.EnterBlock(position);
      var limit = (int)Math.Min(data.Length - position, model.BlockEnd - position);
      var choice = Choose(data, finder, model, position, limit);
      var next = model.Step(output, position, choice);
      finder.Insert(next);
      position = next;
    }

    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: 0.4 ALZ encoder lost track of its model.");
    return coder.Finish();
  }

  private static UharcAlzChoice Choose(byte[] data, UharcMatchFinder finder, Uharc04AlzModel<UharcEncoding> model, int position, int limit) {
    // As for 0.6: reach back at most the window less one longest copy.
    var reach = model.DictionarySize - UharcMatchFinder.MaxLength;
    var best = new UharcAlzChoice(4, 0, 0, 0, data[position]);
    if (position == 0)
      return best;

    var bestScore = 0.0;
    bool Codable(int token, uint distance, int length) {
      var forced = model.ForcedLengthSymbol(position, token, distance);
      return forced < 0 || Uharc04AlzModel<UharcEncoding>.LengthInSymbol(length, forced);
    }

    for (var slot = 0; slot < 12; ++slot) {
      var distance = model.Recent(slot);
      if (distance == uint.MaxValue || distance == 0 || distance > (uint)position || distance > reach)
        continue;
      var token = slot == 0 ? 2 : 3;
      var length = finder.UsableLength(position, distance, limit);
      var score = length - (slot == 0 ? 0.6 : 1.4);
      if (length >= 2 && score > bestScore && Codable(token, distance, length)) {
        best = new(token, 0, slot, length, 0);
        bestScore = score;
      }
    }

    foreach (var distance in finder.Distances(position, reach)) {
      if (!UharcAlzDecoder.CanCodeDistance(distance, position))
        continue;
      var length = finder.UsableLength(position, distance, limit);
      var score = length - 2.0 - Math.Log2(distance) / 6;
      if (length >= 3 && score > bestScore && Codable(1, distance, length)) {
        best = new(1, distance, 0, length, 0);
        bestScore = score;
      }
    }

    if (best.Token == 4 && data[position] == model.Excluded)
      throw new InvalidOperationException("UHARC: 0.4 ALZ encoder reached a byte it cannot code.");
    return best;
  }
}
