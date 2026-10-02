using System.Collections.Concurrent;
using Compression.Registry;

namespace Compression.NativeUI.Maintenance;

/// <summary>One answer to "can this target do that?": yes or no, and the sentence that says why.</summary>
internal readonly record struct Capability(bool Supported, string Reason) {
  public static Capability Yes(string detail) => new(true, detail);
  public static Capability No(string reason) => new(false, reason);
}

/// <summary>
/// What one format offers the Defragment tab: which operations, which defragmentation strategies,
/// which layout options and which ways of optimizing. The ribbon enables exactly what this says
/// and nothing else, so an option is never offered only to be ignored or refused.
/// </summary>
/// <remarks>
/// This is the single place that turns the registry's answer —
/// <see cref="MaintenanceCapabilities.Describe(string)"/>, a <see cref="MaintenanceProfile"/> — into
/// the ribbon's terms. The owner's names map onto the registry's operations as: Defragment →
/// <see cref="MaintenanceCapability.DefragmentExtents"/> (and Sort Entries →
/// <see cref="MaintenanceCapability.SortDirectoryEntries"/>), Optimize → Compress, Repack or
/// Canonicalize as offered, Clear → <see cref="MaintenanceCapability.WipeUnused"/>, and Shrink, Purge,
/// Compact and Scramble by their own names.
/// </remarks>
internal sealed record TargetCapabilities(
  MaintenanceProfile Profile,
  IReadOnlyDictionary<MaintenanceVerb, Capability> Verbs,
  IReadOnlyDictionary<DefragStrategy, Capability> Strategies,
  Capability PackAtStart,
  Capability PackAtEnd,
  Capability Interleave,
  Capability MetadataZone,
  Capability LayoutProfile,
  IReadOnlyList<OptimizeMethod> OptimizeMethods) {

  /// <summary>Whether any operation at all can run on the target.</summary>
  public bool AnySupported => this.Verbs.Values.Any(v => v.Supported);

  public Capability Verb(MaintenanceVerb verb) => this.Verbs.TryGetValue(verb, out var c) ? c : Capability.No("Not offered.");
  public Capability Strategy(DefragStrategy strategy) => this.Strategies.TryGetValue(strategy, out var c) ? c : Capability.No("Not offered.");

  private static readonly ConcurrentDictionary<string, TargetCapabilities> Cache = new(StringComparer.Ordinal);

  /// <summary>
  /// The capabilities of the format registered as <paramref name="formatId"/>. They depend on the
  /// descriptor alone, and the shell asks on every command requery, so they are worked out once.
  /// </summary>
  public static TargetCapabilities For(string formatId) {
    ArgumentNullException.ThrowIfNull(formatId);
    return Cache.GetOrAdd(formatId, Evaluate);
  }

  private static TargetCapabilities Evaluate(string formatId) {
    var descriptor = FormatRegistry.GetById(formatId);
    var profile = descriptor is null
      ? new MaintenanceProfile(formatId, MaintenanceCapability.None, DefragFeature.None, [])
      : MaintenanceCapabilities.Describe(descriptor);
    return From(profile, descriptor?.DisplayName is { Length: > 0 } display ? display : formatId);
  }

  /// <summary>Turns a registry profile into the ribbon's answers.</summary>
  internal static TargetCapabilities From(MaintenanceProfile profile, string name) {
    bool Has(MaintenanceCapability capability) => profile.Supports(capability);
    bool Honours(DefragFeature feature) => profile.Supports(feature);

    var extents = Has(MaintenanceCapability.DefragmentExtents);
    var sort = Has(MaintenanceCapability.SortDirectoryEntries);
    OptimizeMethod[] methods = [
      .. Has(MaintenanceCapability.Compress) ? [OptimizeMethod.Compress] : Array.Empty<OptimizeMethod>(),
      .. Has(MaintenanceCapability.Repack) ? [OptimizeMethod.Repack] : Array.Empty<OptimizeMethod>(),
      .. Has(MaintenanceCapability.Canonicalize) ? [OptimizeMethod.Canonicalize] : Array.Empty<OptimizeMethod>(),
    ];

    var verbs = new Dictionary<MaintenanceVerb, Capability> {
      [MaintenanceVerb.Defragment] = extents || sort
        ? Capability.Yes(extents ? $"Moves {name}'s extents in place; size and contents unchanged." : $"Sorts {name}'s directory entries in place.")
        : Capability.No($"{name} neither moves extents in place nor sorts directory entries."),
      [MaintenanceVerb.Optimize] = methods.Length > 0
        ? Capability.Yes($"Optimizes by {string.Join(", ", methods.Select(Describe))}; the original is replaced only by a verified result.")
        : Capability.No($"{name} has no lossless compress, repack or canonical form."),
      [MaintenanceVerb.Shrink] = Has(MaintenanceCapability.Shrink)
        ? Capability.Yes("Reduces the container to what it needs, keeping every file.")
        : Capability.No($"{name} cannot shrink losslessly."),
      [MaintenanceVerb.Purge] = Has(MaintenanceCapability.Purge)
        ? Capability.Yes("Erases every file, leaving a valid empty container. Asks first.")
        : Capability.No($"{name} has no valid empty state to purge to."),
      [MaintenanceVerb.WipeEmpty] = Has(MaintenanceCapability.WipeUnused)
        ? Capability.Yes("Zeroes unused space — free clusters, slack, dead bytes — and nothing else.")
        : Capability.No($"{name} exposes no layout to tell free space from live data."),
      [MaintenanceVerb.Compact] = Has(MaintenanceCapability.Compact)
        ? Capability.Yes("Defragment extents, compress and shrink in one pass, each where offered.")
        : Capability.No($"{name} offers none of the steps Compact is made of."),
      [MaintenanceVerb.Scramble] = Has(MaintenanceCapability.Scramble)
        ? Capability.Yes("Scatters every block on purpose, so Defragment has something to undo. Contents preserved; asks first.")
        : Capability.No($"{name} cannot scatter a volume in place."),
    };

    Capability Mode(DefragFeature feature, string yes, string what)
      => extents && Honours(feature) ? Capability.Yes(yes) : Capability.No($"{name} cannot {what}.");

    var start = extents && Honours(DefragFeature.ConsolidateAtStart);
    var end = extents && Honours(DefragFeature.ConsolidateAtEnd);
    var strategies = new Dictionary<DefragStrategy, Capability> {
      [DefragStrategy.Consolidate] = start || end
        ? Capability.Yes("Packs every file at one end; free space ends up in one piece.")
        : Capability.No($"{name} cannot pack its files together."),
      [DefragStrategy.Defrag] = Mode(DefragFeature.FillHolesLazy, "Fills existing holes from the tail, moving as few bytes as possible.", "fill holes lazily"),
      [DefragStrategy.Reorder] = Mode(DefragFeature.AscendingOrder, "Moves only what reads backwards, so every file's blocks ascend.", "reorder blocks into ascending order"),
      [DefragStrategy.SortEntries] = sort
        ? Capability.Yes("Sorts every directory's entries by name in place; no data moves.")
        : Capability.No($"{name} does not offer sorting directory entries."),
      [DefragStrategy.CarveHole] = Mode(DefragFeature.CarveHole, "Reserves one contiguous free region of the size you choose.", "carve a hole"),
    };

    return new(
      profile,
      verbs,
      strategies,
      PackAtStart: Mode(DefragFeature.ConsolidateAtStart, "Packs at the front; free space ends up after the last file.", "pack at the start"),
      PackAtEnd: Mode(DefragFeature.ConsolidateAtEnd, "Packs at the back instead; free space stays at the front.", "pack at the end"),
      Interleave: Mode(DefragFeature.Interleave, $"{name} honours block interleave.", "interleave blocks"),
      MetadataZone: Mode(DefragFeature.MetadataZone, $"{name} honours metadata placement.", "place metadata"),
      LayoutProfile: Mode(DefragFeature.LayoutTemplate, $"{name} honours layout profiles.", "follow a layout profile"),
      methods);
  }

  internal static string Describe(OptimizeMethod method) => method switch {
    OptimizeMethod.Compress => "re-encoding with the best compression",
    OptimizeMethod.Repack => "repacking without dead space",
    _ => "rewriting into canonical form",
  };

  /// <summary>
  /// The object that carries <paramref name="capability"/> for <paramref name="formatId"/>: the
  /// descriptor, or its archive or stream operations object.
  /// </summary>
  internal static object? Carrier(string formatId, MaintenanceCapability capability) {
    var descriptor = FormatRegistry.GetById(formatId);
    foreach (var candidate in new object?[] { descriptor, FormatRegistry.GetArchiveOps(formatId), FormatRegistry.GetStreamOps(formatId) })
      if (candidate is not null && MaintenanceCapabilities.Of(candidate).HasFlag(capability))
        return candidate;
    return null;
  }
}
