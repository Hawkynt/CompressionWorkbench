namespace Compression.Registry;

public enum LegacyOptimizeEffect {
  None,
  Compress,
  Canonicalize,
  Repack,
  Ambiguous,
}


/// <summary>
/// Central capability discovery for operations that used to be presented under
/// the ambiguous "Optimize" label.
/// </summary>
public static class OptimizationCapabilities {
  public static bool CanCompress(IFormatDescriptor? descriptor)
    => descriptor is ICompressionOptimizable;

  public static bool CanCanonicalize(IFormatDescriptor? descriptor)
    => descriptor is IArchiveCanonicalizable;

  public static bool CanRepack(IFormatDescriptor? descriptor)
    => descriptor is IArchiveRepackable;

  /// <summary>
  /// Resolves the former umbrella <c>Optimize</c> verb to exactly one explicit
  /// rewrite effect. Overlapping capabilities are intentionally ambiguous.
  /// </summary>
  public static LegacyOptimizeEffect ResolveLegacyOptimizeEffect(IFormatDescriptor? descriptor) {
    var compress = CanCompress(descriptor);
    var canonicalize = CanCanonicalize(descriptor);
    var repack = CanRepack(descriptor);
    var effects = (compress ? 1 : 0) + (canonicalize ? 1 : 0) + (repack ? 1 : 0);
    if (effects == 0)
      return LegacyOptimizeEffect.None;
    if (effects > 1)
      return LegacyOptimizeEffect.Ambiguous;
    if (compress)
      return LegacyOptimizeEffect.Compress;
    if (canonicalize)
      return LegacyOptimizeEffect.Canonicalize;
    return LegacyOptimizeEffect.Repack;
  }

  public static bool CanLegacyOptimizeUnambiguously(IFormatDescriptor? descriptor)
    => ResolveLegacyOptimizeEffect(descriptor) is
      LegacyOptimizeEffect.Compress or
      LegacyOptimizeEffect.Canonicalize or
      LegacyOptimizeEffect.Repack;

  public static bool CanSortDirectoryEntries(IFormatDescriptor? descriptor)
    => descriptor is IFilesystemDirectoryOrderer;

  /// <summary>
  /// Resolves the explicit physical-move capability for a format. A descriptor
  /// either implements <see cref="IFilesystemBlockMover"/> itself or names the
  /// concrete composed mover with <see cref="FilesystemBlockMoverAttribute"/>.
  /// No class-name or namespace convention is treated as a capability.
  /// </summary>
  public static Type? GetFilesystemBlockMoverType(IFormatDescriptor? descriptor) {
    if (descriptor is null)
      return null;
    if (descriptor is IFilesystemBlockMover)
      return descriptor.GetType();

    var attribute = Attribute.GetCustomAttribute(
      descriptor.GetType(), typeof(FilesystemBlockMoverAttribute), inherit: false)
      as FilesystemBlockMoverAttribute;
    if (attribute is null)
      return null;

    var moverType = attribute.MoverType;
    if (!moverType.IsClass
        || moverType.IsAbstract
        || !typeof(IFilesystemBlockMover).IsAssignableFrom(moverType))
      throw new InvalidOperationException(
        $"{descriptor.Id} declares invalid filesystem block mover type '{moverType.FullName}'.");

    return moverType;
  }

  public static bool HasFilesystemBlockMover(IFormatDescriptor? descriptor)
    => GetFilesystemBlockMoverType(descriptor) is not null;

  public static bool CanDefragmentExtents(IFormatDescriptor? descriptor)
    => descriptor is IArchiveDefragmentable && HasFilesystemBlockMover(descriptor);

  /// <summary>
  /// Gets only the writer options whose effect is allocation geometry.
  /// Creation metadata, compatibility constraints and compression parameters are
  /// deliberately excluded even when the same descriptor exposes them.
  /// </summary>
  public static IReadOnlyList<FormatOptionDescriptor> GetAllocationGeometryOptions(IFormatDescriptor? descriptor) {
    if (descriptor is not ILayoutOptimizable
        || descriptor is not IArchiveCreatable
        || descriptor is not IFormatOptionsSchema schema)
      return [];

    return schema.OptionsSchema.Where(static option => option.IsAllocationGeometry).ToArray();
  }

  public static bool CanChangeAllocationGeometry(IFormatDescriptor? descriptor)
    => GetAllocationGeometryOptions(descriptor).Count > 0;
}
