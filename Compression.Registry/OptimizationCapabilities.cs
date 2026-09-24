namespace Compression.Registry;

/// <summary>
/// Central capability discovery for operations that used to be presented under
/// the ambiguous "Optimize" label.
/// </summary>
public static class OptimizationCapabilities {
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

  public static bool CanDefragmentExtents(IFormatDescriptor? descriptor)
    => descriptor is IArchiveDefragmentable && descriptor is IFilesystemBlockMover;

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
