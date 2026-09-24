namespace Compression.Registry;

/// <summary>
/// A logical extent in an archive entry. <see cref="IsHole"/> distinguishes
/// sparse holes from stored data ranges without imposing a filesystem-specific
/// allocation-unit model.
/// </summary>
public readonly record struct ArchiveSparseExtent(long Offset, long Length, bool IsHole);
