namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the ALZ stream (docs/UHARC-ON-DISK.md, "ALZ" and "Writing"): it runs the
/// decoder's <see cref="UharcAlzModel{TCoder}"/> step for step and answers each step with a
/// greedy choice — a copy at the last distance, one of the older distances or a new one found
/// by a hash chain, or a literal. It uses selector 0 (no multimedia predictor).
/// </summary>
internal static class UharcAlzEncoder {

  /// <summary>Packs <paramref name="data"/> as ALZ stream <paramref name="method"/> (40..87).</summary>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new UharcRangeEncoder();
    var model = new UharcAlzModel<UharcEncoding>(new(coder), method, selector: 0);
    var finder = new UharcMatchFinder(data, model.DictionarySize);
    var resets = new UharcModelResets(fileEnds, data.Length, UharcModelResets.AlzSpan, mergeSmallFiles: false);
    var output = new byte[data.Length];
    var position = 0;
    while (position < data.Length) {
      if (resets.IsResetPoint(position))
        model.ResetForNextFile(selector: 0);

      var limit = (int)Math.Min(data.Length - position, position < resets.Next ? resets.Next - position : data.Length - position);
      var choice = Choose(data, finder, model, position, limit);
      var next = model.Step(output, position, choice);
      finder.Insert(next);
      position = next;
    }

    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: ALZ encoder lost track of its model.");
    return coder.Finish();
  }

  private static UharcAlzChoice Choose(byte[] data, UharcMatchFinder finder, UharcAlzModel<UharcEncoding> model, int position, int limit) {
    // UHARC itself reaches back at most the window less one longest copy; both 0.6b extractors
    // mis-copy distances close to the full window.
    var reach = model.DictionarySize - UharcMatchFinder.MaxLength;
    var best = new UharcAlzChoice(4, 0, 0, 0, data[position]);
    if (position == 0)
      return best;

    var bestScore = 0.0;
    for (var slot = 0; slot < 12; ++slot) {
      var distance = model.Recent(slot);
      if (distance == 0 || distance > (uint)position || distance > reach)
        continue;
      var length = finder.UsableLength(position, distance, limit);
      var score = length - (slot == 0 ? 0.6 : 1.4);
      if (length >= 2 && score > bestScore) {
        best = new(slot == 0 ? 2 : 3, 0, slot, length, 0);
        bestScore = score;
      }
    }

    foreach (var distance in finder.Distances(position, reach)) {
      if (!UharcAlzDecoder.CanCodeDistance(distance, position))
        continue;
      var length = finder.UsableLength(position, distance, limit);
      var score = length - 2.0 - Math.Log2(distance) / 6;
      if (length >= UharcAlzModel<UharcEncoding>.MinimumLength(distance) && score > bestScore) {
        best = new(1, distance, 0, length, 0);
        bestScore = score;
      }
    }

    if (best.Token == 4 && data[position] == model.Excluded)
      throw new InvalidOperationException("UHARC: ALZ encoder reached a byte it cannot code.");
    return best;
  }
}
