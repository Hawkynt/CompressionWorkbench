namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the 0.3 ALZ stream (docs/UHARC-ON-DISK.md, "UHARC 0.3 ALZ" and "Writing
/// UHARC 0.3"): it runs the decoder's <see cref="Uharc03AlzModel{TCoder}"/> step for step in
/// literal group 0, never switching models or coding long runs, and answers each step greedily
/// with a copy at a recent distance, a new distance the slot table can express now, or a literal.
/// 0.3 excludes no byte after a copy, so copies need not be whole.
/// </summary>
internal static class Uharc03AlzEncoder {

  /// <summary>Packs <paramref name="data"/> as 0.3 ALZ stream <paramref name="method"/> (36..77).</summary>
  public static byte[] Encode(byte[] data, int method) {
    if (method is < UharcMethods.Alz03First or > 77)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new Uharc03RangeEncoder();
    var model = new Uharc03AlzModel<Uharc03Encoding>(new(coder), method);
    var finder = new UharcMatchFinder(data, (uint)UharcMethods.DictionarySize03(method));
    var output = new byte[data.Length];
    var position = 0;
    while (position < data.Length) {
      var choice = Choose(data, finder, model, position);
      var next = model.Step(output, position, choice);
      finder.Insert(next);
      position = next;
    }

    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: 0.3 ALZ encoder lost track of its model.");
    return coder.Finish();
  }

  private static int MatchLength(byte[] data, int position, uint distance) {
    var source = position - (int)distance;
    var cap = Math.Min(UharcMatchFinder.MaxLength, data.Length - position);
    var length = 0;
    while (length < cap && data[source + length] == data[position + length])
      ++length;
    return length;
  }

  private static UharcAlzChoice Choose(byte[] data, UharcMatchFinder finder, Uharc03AlzModel<Uharc03Encoding> model, int position) {
    var best = new UharcAlzChoice(4, 0, 0, 0, data[position]);
    if (position == 0)
      return best;

    var reach = Math.Min(model.Reach, (uint)position);
    var bestScore = 0.0;
    for (var index = 0; index < model.RecentCount; ++index) {
      var distance = model.Recent(index);
      if (distance == uint.MaxValue || distance == 0 || distance > reach)
        continue;
      var length = MatchLength(data, position, distance);
      var score = length - (index == 0 ? 0.6 : 1.4);
      if (length >= 2 && score > bestScore) {
        best = new(3, 0, index, length, 0);
        bestScore = score;
      }
    }

    foreach (var distance in finder.Distances(position, reach)) {
      if (!model.CanCodeDistance(distance))
        continue;
      var length = MatchLength(data, position, distance);
      var score = length - 2.0 - Math.Log2(distance) / 6;
      if (length >= 3 && score > bestScore) {
        best = new(1, distance, 0, length, 0);
        bestScore = score;
      }
    }

    return best;
  }
}
