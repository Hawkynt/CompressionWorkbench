namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the 0.3 ALZ stream (docs/UHARC-ON-DISK.md, "UHARC 0.3 ALZ" and "Writing
/// UHARC 0.3"): it runs the decoder's <see cref="Uharc03AlzModel{TCoder}"/> step for step and
/// answers each step greedily with a copy at a recent distance, a new distance the slot table
/// can express now, or a literal. At the start of each file whose multimedia class asks for
/// another literal group it switches the model to that group (see <see cref="SwitchesFor"/>); it
/// never codes long runs. 0.3 excludes no byte after a copy, so copies need not be whole.
/// </summary>
internal static class Uharc03AlzEncoder {

  /// <summary>Packs <paramref name="data"/> as 0.3 ALZ stream <paramref name="method"/> (36..77).</summary>
  /// <param name="data">The stream.</param>
  /// <param name="method">The 0.3 ALZ method.</param>
  /// <param name="fileEnds">Where each file ends in the stream; each file starts in the literal group of its class.</param>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.Alz03First or > 77)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new Uharc03RangeEncoder();
    var model = new Uharc03AlzModel<Uharc03Encoding>(new(coder), method);
    var finder = new UharcMatchFinder(data, (uint)UharcMethods.DictionarySize03(method));
    var output = new byte[data.Length];
    var position = 0;
    long fileStart = 0;
    var file = 0;
    while (position < data.Length) {
      // A file starting here (empty files start and end at once) sets the literal group.
      while (file < fileEnds.Count && fileStart <= position) {
        if (fileStart == position && fileEnds[file] > fileStart)
          foreach (var value in SwitchesFor(UharcMediaClass.Of(data.AsSpan((int)fileStart, (int)(fileEnds[file] - fileStart))), model.Group))
            model.Select(value, position);
        fileStart = fileEnds[file++];
      }

      var nextStart = file < fileEnds.Count ? fileStart : data.Length;
      var choice = Choose(data, finder, model, position, (int)Math.Min(nextStart, data.Length));
      var next = model.Step(output, position, choice);
      finder.Insert(next);
      position = next;
    }

    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: 0.3 ALZ encoder lost track of its model.");
    return coder.Finish();
  }

  /// <summary>
  /// The model switches for a file of multimedia class <paramref name="mediaClass"/> while the
  /// model codes in <paramref name="group"/>: none when the group already fits, else the group —
  /// 3 (three bytes back) for a 24-bit BMP, 5 (one back) for 8-bit mono WAV, 2 (two back) for
  /// 8-bit stereo and 16-bit mono, 4 (four back) for 16-bit stereo, 0 otherwise — and the step
  /// UHARC 0.3 pairs with it (128, or 32 for 16-bit mono; 16-bit stereo keeps the group's 8).
  /// </summary>
  internal static IEnumerable<int> SwitchesFor(int mediaClass, int group) {
    var (wanted, step) = mediaClass switch {
      3 => (3, 9),
      5 => (5, 9),
      7 => (2, 9),
      8 => (2, 8),
      9 => (4, -1),
      _ => (0, 9),
    };
    if (wanted == group)
      yield break;
    yield return wanted;
    if (step >= 0)
      yield return step;
  }

  private static int MatchLength(byte[] data, int position, uint distance, int end) {
    var source = position - (int)distance;
    var cap = Math.Min(UharcMatchFinder.MaxLength, end - position);
    var length = 0;
    while (length < cap && data[source + length] == data[position + length])
      ++length;
    return length;
  }

  private static UharcAlzChoice Choose(byte[] data, UharcMatchFinder finder, Uharc03AlzModel<Uharc03Encoding> model, int position, int end) {
    var best = new UharcAlzChoice(4, 0, 0, 0, data[position]);
    if (position == 0)
      return best;

    var reach = Math.Min(model.Reach, (uint)position);
    var bestScore = 0.0;
    for (var index = 0; index < model.RecentCount; ++index) {
      var distance = model.Recent(index);
      if (distance == uint.MaxValue || distance == 0 || distance > reach)
        continue;
      var length = MatchLength(data, position, distance, end);
      var score = length - (index == 0 ? 0.6 : 1.4);
      if (length >= 2 && score > bestScore) {
        best = new(3, 0, index, length, 0);
        bestScore = score;
      }
    }

    foreach (var distance in finder.Distances(position, reach)) {
      if (!model.CanCodeDistance(distance))
        continue;
      var length = MatchLength(data, position, distance, end);
      var score = length - 2.0 - Math.Log2(distance) / 6;
      if (length >= 3 && score > bestScore) {
        best = new(1, distance, 0, length, 0);
        bestScore = score;
      }
    }

    return best;
  }
}
