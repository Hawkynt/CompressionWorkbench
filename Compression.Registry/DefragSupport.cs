namespace Compression.Registry;

/// <summary>
/// The parts of a <see cref="DefragOptions"/> request a defragmenter can honour.
/// </summary>
[Flags]
public enum DefragFeature {
  /// <summary>Nothing beyond the request's defaults.</summary>
  None = 0,
  /// <summary><see cref="DefragMode.ConsolidateAtStart"/>.</summary>
  ConsolidateAtStart = 1 << 0,
  /// <summary><see cref="DefragMode.ConsolidateAtEnd"/>.</summary>
  ConsolidateAtEnd = 1 << 1,
  /// <summary><see cref="DefragMode.FillHolesLazy"/>.</summary>
  FillHolesLazy = 1 << 2,
  /// <summary><see cref="DefragMode.CarveHole"/>, including an explicit <see cref="DefragOptions.HoleAt"/>.</summary>
  CarveHole = 1 << 3,
  /// <summary><see cref="DefragMode.AscendingOrder"/>.</summary>
  AscendingOrder = 1 << 4,
  /// <summary>A <see cref="DefragOptions.InterleaveStride"/> above one.</summary>
  Interleave = 1 << 5,
  /// <summary>A <see cref="DefragOptions.MetadataZonePlacement"/> other than <see cref="MetadataZone.Unchanged"/>.</summary>
  MetadataZone = 1 << 6,
  /// <summary>A <see cref="DefragOptions.LayoutTemplate"/>.</summary>
  LayoutTemplate = 1 << 7,

  /// <summary>The three packing modes every planner-driven mover offers.</summary>
  Packing = ConsolidateAtStart | ConsolidateAtEnd | FillHolesLazy,
}

/// <summary>
/// Refuses a defragmentation request that asks for something the format would
/// otherwise quietly ignore.
/// </summary>
/// <remarks>
/// A layout option that is accepted and then not applied is indistinguishable,
/// from outside, from one that was applied: the call returns, the files read
/// back, and the volume is not laid out the way the caller asked. Naming the
/// unsupported part up front — before anything is moved — is the honest answer,
/// and it is the answer callers already treat as "this format does not lay out
/// that way" (<see cref="NotSupportedException"/>).
/// </remarks>
public static class DefragSupport {

  /// <summary>
  /// Throws <see cref="NotSupportedException"/> when <paramref name="options"/> asks
  /// for a mode or option outside <paramref name="supported"/>.
  /// </summary>
  public static void Require(DefragOptions options, DefragFeature supported, string format) {
    ArgumentNullException.ThrowIfNull(options);
    var missing = new List<string>();
    var mode = options.Mode switch {
      DefragMode.ConsolidateAtStart => DefragFeature.ConsolidateAtStart,
      DefragMode.ConsolidateAtEnd => DefragFeature.ConsolidateAtEnd,
      DefragMode.FillHolesLazy => DefragFeature.FillHolesLazy,
      DefragMode.CarveHole => DefragFeature.CarveHole,
      DefragMode.AscendingOrder => DefragFeature.AscendingOrder,
      _ => DefragFeature.None,
    };
    if ((supported & mode) == 0 || mode == DefragFeature.None) missing.Add($"the {options.Mode} mode");
    if (options.InterleaveStride > 1 && (supported & DefragFeature.Interleave) == 0)
      missing.Add($"block interleave (stride {options.InterleaveStride})");
    if (options.MetadataZonePlacement != MetadataZone.Unchanged && (supported & DefragFeature.MetadataZone) == 0)
      missing.Add($"metadata placement ({options.MetadataZonePlacement})");
    if (options.LayoutTemplate != null && (supported & DefragFeature.LayoutTemplate) == 0)
      missing.Add("layout templates");
    if (missing.Count > 0)
      throw new NotSupportedException(
        $"{format} cannot defragment with {string.Join(", ", missing)}; the volume was left unchanged.");
  }
}
