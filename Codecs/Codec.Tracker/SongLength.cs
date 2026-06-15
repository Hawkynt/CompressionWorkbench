#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// Deterministic song-length estimator. Walks the order list one row at a time,
/// honouring position jumps (Bxx / B), pattern breaks (Dxx / C) and the pattern
/// loop (E6x / SBx), accumulating row time from the current speed/tempo. Stops at
/// the first revisit of an (order, row) pair already seen with no outstanding loop
/// state, at the natural end of the order list, or at a hard 10-minute cap so an
/// infinite jump still yields a finite, deterministic duration.
/// </summary>
internal static class SongLength {

  public const double HardCapSeconds = 600.0;

  /// <summary>Estimated playback duration in seconds for the song's order-0 start.</summary>
  public static double Estimate(TrackerSong song) {
    if (song.Order.Length == 0 || song.Patterns.Length == 0)
      return 0;

    var speed = song.InitialSpeed;
    var tempo = song.InitialTempo;
    double seconds = 0;

    var orderIndex = 0;
    var row = 0;
    var visited = new HashSet<(int order, int row)>();

    // Per-channel pattern-loop bookkeeping (E6x / SBx).
    var loopRow = new int[song.Channels];
    var loopCount = new int[song.Channels];

    var guard = 0;
    const int guardLimit = 1_000_000;

    while (seconds < HardCapSeconds && ++guard < guardLimit) {
      if (orderIndex >= song.Order.Length)
        break;

      var patternIdx = song.Order[orderIndex];
      if (patternIdx < 0 || patternIdx >= song.Patterns.Length) {
        ++orderIndex;
        continue;
      }

      var key = (orderIndex, row);
      var anyLoopActive = false;
      for (var ch = 0; ch < song.Channels; ++ch)
        if (loopCount[ch] > 0) { anyLoopActive = true; break; }
      // Only treat a revisit as a terminating loop when no pattern loop is active,
      // so finite E6x/SBx repeats are counted rather than cut short.
      if (!anyLoopActive && !visited.Add(key))
        break;

      var pattern = song.Patterns[patternIdx];
      if (row >= pattern.Rows) {
        row = 0;
        ++orderIndex;
        continue;
      }

      // Row time: speed ticks at the tempo's tick rate (BPM × 2 / 5 Hz).
      var tickRate = tempo * 2.0 / 5.0;
      var patternDelay = 0;

      var jumpOrder = -1;
      var breakRow = -1;

      for (var ch = 0; ch < song.Channels; ++ch) {
        ref var cell = ref pattern.Cell(row, ch);
        ApplyFlow(song, cell, ch, row, ref speed, ref tempo, ref jumpOrder, ref breakRow, ref patternDelay, loopRow, loopCount);
      }

      seconds += speed * (patternDelay + 1) / tickRate;

      // Pattern-loop jump (E6x / SBx) takes priority within the same pattern.
      var loopJumped = false;
      for (var ch = 0; ch < song.Channels; ++ch) {
        if (loopCount[ch] > 0) {
          row = loopRow[ch];
          loopJumped = true;
          break;
        }
      }
      if (loopJumped)
        continue;

      if (jumpOrder >= 0) {
        orderIndex = jumpOrder;
        row = breakRow >= 0 ? breakRow : 0;
        continue;
      }
      if (breakRow >= 0) {
        ++orderIndex;
        row = breakRow;
        continue;
      }

      ++row;
      if (row >= pattern.Rows) {
        row = 0;
        ++orderIndex;
      }
    }

    return seconds;
  }

  private static void ApplyFlow(
      TrackerSong song, TrackerCell cell, int channel, int currentRow,
      ref int speed, ref int tempo, ref int jumpOrder, ref int breakRow, ref int patternDelay,
      int[] loopRow, int[] loopCount) {

    var effect = cell.Effect;
    var param = cell.EffectParam;
    var x = param >> 4;
    var y = param & 0x0F;

    if (song.Kind == TrackerKind.Mod) {
      switch (effect) {
        case 0xB: jumpOrder = param; break;
        case 0xD: breakRow = x * 10 + y; break;
        case 0xF:
          if (param < 0x20) { if (param > 0) speed = param; } else tempo = param;
          break;
        case 0xE:
          if (x == 0x6) HandleLoop(channel, y, currentRow, loopRow, loopCount);
          else if (x == 0xE) patternDelay = y;
          break;
      }
    } else {
      switch (effect) {
        case 0x1: if (param > 0) speed = param; break; // A
        case 0x2: jumpOrder = param; break;            // B
        case 0x3: breakRow = x * 10 + y; break;        // C
        case 0x14: if (param >= 0x20) tempo = param; break; // T
        case 0x13: // S
          if (x == 0xB) HandleLoop(channel, y, currentRow, loopRow, loopCount);
          else if (x == 0xE) patternDelay = y;
          break;
      }
    }
  }

  // E6x / SBx pattern loop: y==0 sets the loop start row; y>0 repeats y times.
  private static void HandleLoop(int channel, int y, int currentRow, int[] loopRow, int[] loopCount) {
    if (y == 0) {
      loopRow[channel] = currentRow; // record loop start
      return;
    }
    if (loopCount[channel] == 0)
      loopCount[channel] = y; // begin: repeat y times
    else
      --loopCount[channel];   // one repeat consumed
  }
}
