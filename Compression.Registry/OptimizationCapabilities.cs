namespace Compression.Registry;

/// <summary>
/// Central capability discovery for operations that used to be presented under
/// the ambiguous "Optimize" label.
/// </summary>
public static class OptimizationCapabilities {
  private static readonly object BlockMoverCacheGate = new();
  private static readonly Dictionary<Type, Type?> BlockMoverTypeCache = [];
  public static bool CanCompress(IFormatDescriptor? descriptor) {
    if (descriptor is ICompressionOptimizable)
      return true;

    return descriptor?.TarCompressionFormatId is { Length: > 0 } outerFormatId
      && FormatRegistry.GetById(outerFormatId) is ICompressionOptimizable;
  }

  public static bool CanCanonicalize(IFormatDescriptor? descriptor)
    => descriptor is IArchiveCanonicalizable;

  public static bool CanRepack(IFormatDescriptor? descriptor)
    => descriptor is IArchiveRepackable;

  public static bool CanSortDirectoryEntries(IFormatDescriptor? descriptor)
    => descriptor is IFilesystemDirectoryOrderer;

  /// <summary>
  /// Resolves the explicit physical-move capability for a format. Most mature
  /// descriptors expose <see cref="IFilesystemBlockMover"/> directly; older
  /// format implementations keep the mover as a separate concrete helper in
  /// the same namespace. A namespace must contain exactly one concrete mover
  /// for that helper to count, so ambiguous conventions fail closed.
  /// </summary>
  public static Type? GetFilesystemBlockMoverType(IFormatDescriptor? descriptor) {
    if (descriptor is null)
      return null;
    if (descriptor is IFilesystemBlockMover)
      return descriptor.GetType();

    var descriptorType = descriptor.GetType();
    lock (BlockMoverCacheGate) {
      if (BlockMoverTypeCache.TryGetValue(descriptorType, out var cached))
        return cached;

      var candidates = descriptorType.Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false }
                       && type.Namespace == descriptorType.Namespace
                       && typeof(IFilesystemBlockMover).IsAssignableFrom(type))
        .ToArray();
      var result = candidates.Length == 1 ? candidates[0] : null;
      BlockMoverTypeCache[descriptorType] = result;
      return result;
    }
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
