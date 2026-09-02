#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Structural binding used to reach the common filesystem-driver contract.
/// This deliberately says nothing about the exact image profile: probing an
/// image can still refuse a damaged/unsupported feature set.
/// </summary>
public enum FilesystemDriverBindingKind {
  None,
  ArchiveProjection,
  SidecarNative,
  DescriptorNative,
}

/// <summary>
/// Machine-readable repository coverage for one FileSystem.* descriptor.
/// It answers whether the implementation has a path to an IFilesystemSession
/// and which lower-level primitives are already available for finishing a
/// native read/write driver.
/// </summary>
public sealed record FilesystemDriverCoverage(
  string FormatId,
  string DisplayName,
  FilesystemDriverBindingKind Binding,
  bool HasArchiveProjection,
  bool HasArchiveMutation,
  bool HasExtentMap,
  bool HasBlockMover,
  bool HasBlockDeviceProvider,
  bool HasNativeReadinessProvider
) {
  public bool HasDriverPath => Binding != FilesystemDriverBindingKind.None;
  public bool IsNative => Binding is FilesystemDriverBindingKind.DescriptorNative or FilesystemDriverBindingKind.SidecarNative;
}
