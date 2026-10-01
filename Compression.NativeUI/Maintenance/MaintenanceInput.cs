using System.Globalization;

namespace Compression.NativeUI.Maintenance;

/// <summary>A value typed into the ribbon, read: either the value, or the reason it cannot be used.</summary>
internal readonly record struct Parsed<T>(T Value, string? Error) {
  public bool IsValid => this.Error is null;
  public static Parsed<T> Ok(T value) => new(value, null);
  public static Parsed<T> Fail(string error) => new(default!, error);
}

/// <summary>Reads and formats the few values the Defragment tab takes as text.</summary>
internal static class MaintenanceInput {
  public const int MinInterleave = 1;
  public const int MaxInterleave = 256;

  /// <summary>
  /// The block interleave stride: 1 is contiguous, 2 every other block, up to 256. Anything outside
  /// that is refused rather than clamped, so what runs is what was typed.
  /// </summary>
  public static Parsed<int> ParseInterleave(string? text) {
    var trimmed = text?.Trim() ?? "";
    if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var stride))
      return Parsed<int>.Fail($"Interleave must be a whole number from {MinInterleave} to {MaxInterleave}.");
    return stride is < MinInterleave or > MaxInterleave
      ? Parsed<int>.Fail($"Interleave {stride} is outside {MinInterleave}–{MaxInterleave}.")
      : Parsed<int>.Ok(stride);
  }

  /// <summary>A byte count with an optional k/m/g suffix (KiB, MiB, GiB): <c>64m</c>, <c>1g</c>, <c>524288</c>.</summary>
  public static Parsed<long> ParseSize(string? text) {
    var s = text?.Trim().ToLowerInvariant() ?? "";
    var multiplier = 1L;
    if (s.EndsWith('k')) (multiplier, s) = (1L << 10, s[..^1]);
    else if (s.EndsWith('m')) (multiplier, s) = (1L << 20, s[..^1]);
    else if (s.EndsWith('g')) (multiplier, s) = (1L << 30, s[..^1]);

    if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
      return Parsed<long>.Fail("Hole size must be a positive size such as 64m, 1g or 524288.");
    return n > long.MaxValue / multiplier
      ? Parsed<long>.Fail("Hole size is too large.")
      : Parsed<long>.Ok(n * multiplier);
  }

  /// <summary>Where a carved hole starts: <c>auto</c> (after the last live extent, -1) or a byte offset.</summary>
  public static Parsed<long> ParseHoleAt(string? text) {
    var s = text?.Trim() ?? "";
    if (s.Length == 0 || s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Parsed<long>.Ok(-1);
    return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
      ? Parsed<long>.Ok(offset)
      : Parsed<long>.Fail("Hole offset must be 'auto' or a byte offset.");
  }

  /// <summary>Scramble's seed: any 32-bit integer. The same seed deals the same layout.</summary>
  public static Parsed<int> ParseSeed(string? text)
    => int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seed)
      ? Parsed<int>.Ok(seed)
      : Parsed<int>.Fail("Seed must be a whole number.");

  public static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };
}
