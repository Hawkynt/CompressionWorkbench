using System.Reflection;

namespace Compression.Registry;

/// <summary>
/// The maintenance operations a format can perform, split by effect. Each flag is backed
/// by exactly one capability interface, so a format advertises an operation by
/// implementing that interface and by nothing else — no name, category or creation
/// option implies one.
/// </summary>
/// <remarks>
/// Every operation here is lossless or refuses: it keeps every name, byte, timestamp,
/// attribute and container property it was not asked to change, or it throws
/// <see cref="NotSupportedException"/> and leaves the target byte for byte as it was.
/// See <c>docs/MAINTENANCE-MECHANISMS.md</c>.
/// </remarks>
[Flags]
public enum MaintenanceCapability {
  /// <summary>No maintenance operation.</summary>
  None = 0,

  /// <summary>Re-encode the payload with the best compression, same format — <see cref="ICompressionOptimizable"/>.</summary>
  Compress = 1 << 0,

  /// <summary>Rewrite into the canonical representation (chunk order, padding, header normal form) — <see cref="IArchiveCanonicalizable"/>.</summary>
  Canonicalize = 1 << 1,

  /// <summary>Rebuild from the same entries, stored bytes copied verbatim, dead space dropped — <see cref="IArchiveRepackable"/>.</summary>
  Repack = 1 << 2,

  /// <summary>Sort directory entries in place without moving data — <see cref="IFilesystemDirectoryOrderer"/>.</summary>
  SortDirectoryEntries = 1 << 3,

  /// <summary>
  /// Move file extents in place; image size and contents unchanged —
  /// <see cref="IArchiveDefragmentable"/> driven by a physical <see cref="IFilesystemBlockMover"/>.
  /// The modes it honours are in <see cref="MaintenanceProfile.DefragFeatures"/>.
  /// </summary>
  DefragmentExtents = 1 << 4,

  /// <summary>
  /// Lay the volume out again at another allocation geometry (cluster size, image size, …) —
  /// <see cref="ILayoutOptimizable"/> whose relayout keeps everything
  /// (<see cref="ILayoutOptimizable.RelayoutPreservesEverything"/>), with at least one option tagged
  /// <see cref="FormatOptionDescriptor.IsAllocationGeometry"/>. The settable keys are in
  /// <see cref="MaintenanceProfile.GeometryOptions"/>.
  /// </summary>
  ChangeGeometry = 1 << 5,

  /// <summary>Reduce the container to what it needs — <see cref="IArchiveShrinkable"/>.</summary>
  Shrink = 1 << 6,

  /// <summary>Remove every live entry, leaving a valid empty container — <see cref="IArchivePurgeable"/>.</summary>
  Purge = 1 << 7,

  /// <summary>Zero what no live entry holds — <see cref="IWipeEmpty"/>.</summary>
  WipeUnused = 1 << 8,

  /// <summary>Scatter every block on purpose, for testing a defragmenter — <see cref="IFilesystemScrambleable"/>.</summary>
  Scramble = 1 << 9,

  /// <summary>
  /// The composite: defragment extents, then compress, then shrink — offered wherever any
  /// of the three is. It never changes geometry; that is <see cref="ChangeGeometry"/>.
  /// </summary>
  Compact = 1 << 10,
}

/// <summary>
/// What one format can do by way of maintenance, in the shape a shell or CLI needs to
/// enable its commands: the operations, the defragmentation modes, and the geometry keys.
/// </summary>
/// <param name="FormatId">The registry id the profile describes.</param>
/// <param name="Operations">Every operation the format performs losslessly or refuses.</param>
/// <param name="DefragFeatures">The <see cref="DefragOptions"/> the extent defragmenter honours;
/// <see cref="DefragFeature.None"/> when <see cref="MaintenanceCapability.DefragmentExtents"/> is absent.</param>
/// <param name="GeometryOptions">The options a geometry change may set; empty when
/// <see cref="MaintenanceCapability.ChangeGeometry"/> is absent.</param>
public sealed record MaintenanceProfile(
    string FormatId,
    MaintenanceCapability Operations,
    DefragFeature DefragFeatures,
    IReadOnlyList<FormatOptionDescriptor> GeometryOptions) {

  /// <summary>Whether every flag in <paramref name="capability"/> is offered.</summary>
  public bool Supports(MaintenanceCapability capability)
    => capability != MaintenanceCapability.None && (this.Operations & capability) == capability;

  /// <summary>Whether the extent defragmenter honours every part of <paramref name="feature"/>.</summary>
  public bool Supports(DefragFeature feature)
    => feature != DefragFeature.None && (this.DefragFeatures & feature) == feature;
}

/// <summary>
/// The one place that answers "which maintenance operations does this format offer".
/// The CLI, the shell and the package support matrices all ask here, so they cannot
/// disagree with each other or with the descriptors.
/// </summary>
public static class MaintenanceCapabilities {

  /// <summary>Describes the format registered under <paramref name="formatId"/>, or null when there is none.</summary>
  public static MaintenanceProfile? Describe(string formatId) {
    ArgumentException.ThrowIfNullOrEmpty(formatId);
    return FormatRegistry.GetById(formatId) is { } descriptor ? Describe(descriptor) : null;
  }

  /// <summary>
  /// Describes <paramref name="descriptor"/>, together with the archive and stream
  /// operation objects the registry holds for its id (which may be separate objects).
  /// </summary>
  public static MaintenanceProfile Describe(IFormatDescriptor descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);
    var targets = Targets(descriptor).ToArray();
    var operations = MaintenanceCapability.None;
    var features = DefragFeature.None;
    IReadOnlyList<FormatOptionDescriptor> geometry = [];
    foreach (var target in targets) {
      operations |= Of(target);
      features |= DefragFeaturesOf(target);
      if (geometry.Count == 0) geometry = GeometryOptionsOf(target);
    }
    return new MaintenanceProfile(descriptor.Id, operations, features, geometry);
  }

  /// <summary>The operations <paramref name="target"/> (a descriptor or an operations object) offers by itself.</summary>
  public static MaintenanceCapability Of(object? target) {
    if (target is null) return MaintenanceCapability.None;
    var result = MaintenanceCapability.None;
    if (target is ICompressionOptimizable { CanOptimizeCompression: true }) result |= MaintenanceCapability.Compress;
    if (target is IArchiveCanonicalizable) result |= MaintenanceCapability.Canonicalize;
    if (target is IArchiveRepackable) result |= MaintenanceCapability.Repack;
    if (target is IFilesystemDirectoryOrderer) result |= MaintenanceCapability.SortDirectoryEntries;
    if (CanDefragmentExtents(target)) result |= MaintenanceCapability.DefragmentExtents;
    if (CanChangeGeometry(target)) result |= MaintenanceCapability.ChangeGeometry;
    if (target is IArchiveShrinkable) result |= MaintenanceCapability.Shrink;
    if (target is IArchivePurgeable { CanPurgeToEmpty: true }) result |= MaintenanceCapability.Purge;
    if (target is IWipeEmpty) result |= MaintenanceCapability.WipeUnused;
    if (target is IFilesystemScrambleable) result |= MaintenanceCapability.Scramble;
    if ((result & (MaintenanceCapability.DefragmentExtents | MaintenanceCapability.Compress | MaintenanceCapability.Shrink)) != 0)
      result |= MaintenanceCapability.Compact;
    return result;
  }

  /// <summary>
  /// Whether <paramref name="target"/> defragments by moving extents in place: it is
  /// <see cref="IArchiveDefragmentable"/>, it has a physical block mover, and it declares
  /// at least one mode it honours. A defragment that writes the container out again is
  /// a rebuild, not an extent move, and does not count.
  /// </summary>
  public static bool CanDefragmentExtents(object? target)
    => target is IArchiveDefragmentable defragmentable
       && BlockMoverTypeOf(target) is not null
       && defragmentable.SupportedDefragFeatures != DefragFeature.None;

  /// <summary>The defragmentation modes and options <paramref name="target"/> honours in place.</summary>
  public static DefragFeature DefragFeaturesOf(object? target)
    => CanDefragmentExtents(target) ? ((IArchiveDefragmentable)target!).SupportedDefragFeatures : DefragFeature.None;

  /// <summary>
  /// The physical block mover behind <paramref name="target"/>'s defragmenter: the target
  /// itself when it implements <see cref="IFilesystemBlockMover"/>, or the type named by its
  /// <see cref="FilesystemBlockMoverAttribute"/>; null when it has none.
  /// </summary>
  public static Type? BlockMoverTypeOf(object? target) {
    if (target is null) return null;
    if (target is IFilesystemBlockMover) return target.GetType();
    var attribute = target.GetType().GetCustomAttribute<FilesystemBlockMoverAttribute>(inherit: false);
    if (attribute is null) return null;
    var mover = attribute.MoverType;
    if (!mover.IsClass || mover.IsAbstract || !typeof(IFilesystemBlockMover).IsAssignableFrom(mover))
      throw new InvalidOperationException(
        $"{target.GetType().FullName} names '{mover.FullName}' as its block mover, which is not a concrete {nameof(IFilesystemBlockMover)}.");
    return mover;
  }

  /// <summary>Whether <paramref name="target"/> can be re-created at another allocation geometry.</summary>
  public static bool CanChangeGeometry(object? target) => GeometryOptionsOf(target).Count > 0;

  /// <summary>
  /// The options a geometry change may set on <paramref name="target"/>: those its schema tags
  /// <see cref="FormatOptionDescriptor.IsAllocationGeometry"/>, provided it can lay itself out
  /// again without losing anything (<see cref="ILayoutOptimizable.RelayoutPreservesEverything"/>,
  /// plus <see cref="IArchiveCreatable"/>). Labels, compression parameters and compatibility
  /// switches are never among them.
  /// </summary>
  public static IReadOnlyList<FormatOptionDescriptor> GeometryOptionsOf(object? target) {
    if (target is not ILayoutOptimizable { RelayoutPreservesEverything: true }
        || target is not (IArchiveCreatable and IArchiveFormatOperations)
        || target is not IFormatOptionsSchema schema)
      return [];
    return [.. schema.OptionsSchema.Where(static o => o.IsAllocationGeometry)];
  }

  private static IEnumerable<object> Targets(IFormatDescriptor descriptor) {
    yield return descriptor;
    if (FormatRegistry.GetArchiveOps(descriptor.Id) is { } archive && !ReferenceEquals(archive, descriptor))
      yield return archive;
    if (FormatRegistry.GetStreamOps(descriptor.Id) is { } stream && !ReferenceEquals(stream, descriptor))
      yield return stream;
  }
}

/// <summary>
/// Names the concrete <see cref="IFilesystemBlockMover"/> a filesystem descriptor's
/// defragmenter drives, when the mover is a separate class rather than the descriptor itself.
/// </summary>
/// <remarks>
/// Implementing <see cref="IFilesystemBlockMover"/> on the descriptor is already an explicit
/// claim and needs no attribute. The attribute is how a descriptor composed with a helper
/// mover states the same thing, so <see cref="MaintenanceCapabilities.CanDefragmentExtents"/>
/// never has to infer a mover from a class name.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FilesystemBlockMoverAttribute(Type moverType) : Attribute {
  /// <summary>The concrete mover type.</summary>
  public Type MoverType { get; } = moverType ?? throw new ArgumentNullException(nameof(moverType));
}
