namespace Compression.Registry;

/// <summary>
/// Makes the default archive input-mode members available when a descriptor is
/// referenced by its concrete type. Default interface members are otherwise only
/// in the member set of an <see cref="IArchiveFormatOperations"/> reference.
/// </summary>
public static class ArchiveFormatOperationsExtensions {
  /// <inheritdoc cref="IArchiveFormatOperations.ListStreaming"/>
  public static List<ArchiveEntryInfo> ListStreaming(
    this IArchiveFormatOperations operations,
    Stream archive,
    string? password)
    => operations.ListStreaming(archive, password);

  /// <inheritdoc cref="IArchiveFormatOperations.ExtractStreaming"/>
  public static void ExtractStreaming(
    this IArchiveFormatOperations operations,
    Stream archive,
    string outputDir,
    string? password,
    string[]? files)
    => operations.ExtractStreaming(archive, outputDir, password, files);

  /// <inheritdoc cref="IArchiveFormatOperations.ListSeekable"/>
  public static List<ArchiveEntryInfo> ListSeekable(
    this IArchiveFormatOperations operations,
    Stream archive,
    string? password)
    => operations.ListSeekable(archive, password);

  /// <inheritdoc cref="IArchiveFormatOperations.ExtractSeekable"/>
  public static void ExtractSeekable(
    this IArchiveFormatOperations operations,
    Stream archive,
    string outputDir,
    string? password,
    string[]? files)
    => operations.ExtractSeekable(archive, outputDir, password, files);

  /// <inheritdoc cref="IArchiveFormatOperations.ListSpan"/>
  public static List<ArchiveEntryInfo> ListSpan(
    this IArchiveFormatOperations operations,
    ReadOnlySpan<byte> archive,
    string? password)
    => operations.ListSpan(archive, password);

  /// <inheritdoc cref="IArchiveFormatOperations.ExtractSpan"/>
  public static void ExtractSpan(
    this IArchiveFormatOperations operations,
    ReadOnlySpan<byte> archive,
    string outputDir,
    string? password,
    string[]? files)
    => operations.ExtractSpan(archive, outputDir, password, files);

  /// <inheritdoc cref="IArchiveFormatOperations.OpenEntryStreaming"/>
  public static Stream OpenEntryStreaming(
    this IArchiveFormatOperations operations,
    Stream archive,
    string entryName,
    string? password)
    => operations.OpenEntryStreaming(archive, entryName, password);

  /// <inheritdoc cref="IArchiveFormatOperations.OpenEntrySeekable"/>
  public static Stream OpenEntrySeekable(
    this IArchiveFormatOperations operations,
    Stream archive,
    string entryName,
    string? password)
    => operations.OpenEntrySeekable(archive, entryName, password);

  /// <inheritdoc cref="IArchiveFormatOperations.OpenEntrySpan"/>
  public static Stream OpenEntrySpan(
    this IArchiveFormatOperations operations,
    ReadOnlySpan<byte> archive,
    string entryName,
    string? password)
    => operations.OpenEntrySpan(archive, entryName, password);

  /// <inheritdoc cref="IArchiveFormatOperations.ExtractEntryToMemoryStreaming"/>
  public static byte[] ExtractEntryToMemoryStreaming(
    this IArchiveFormatOperations operations,
    Stream archive,
    string entryName,
    string? password)
    => operations.ExtractEntryToMemoryStreaming(archive, entryName, password);

  /// <inheritdoc cref="IArchiveFormatOperations.ExtractEntryToMemorySpan"/>
  public static byte[] ExtractEntryToMemorySpan(
    this IArchiveFormatOperations operations,
    ReadOnlySpan<byte> archive,
    string entryName,
    string? password)
    => operations.ExtractEntryToMemorySpan(archive, entryName, password);
}
