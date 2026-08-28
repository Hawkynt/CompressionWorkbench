from pathlib import Path
import re

p = Path("FileSystems/FileSystem.Refs/RefsFormatDescriptor.cs")
s = p.read_text(encoding="utf-8")

old = """public sealed class RefsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IFilesystemExtentMap,
  IArchiveDefragmentable,
  ILayoutOptimizable {"""
new = """public sealed class RefsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveModifiable,
  IFilesystemExtentMap,
  IArchiveDefragmentable,
  ILayoutOptimizable {"""
if old not in s:
    raise SystemExit("ReFS descriptor interface block not found")
s = s.replace(old, new, 1)

old = """  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;"""
new = """  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;"""
if old not in s:
    raise SystemExit("ReFS capability block not found")
s = s.replace(old, new, 1)

s = s.replace(
    'public string Description => "Microsoft ReFS 3.x volume image with namespace, allocation, in-place data relocation and filesystem-metadata placement support.";',
    'public string Description => "Microsoft ReFS 3.x volume image; offline-quiescent existing-file replace/remove plus allocation and metadata placement. Native mounted-driver transactions remain a separate readiness tier.";',
    1,
)

marker = "  private static List<ArchiveEntryInfo> ListDiagnosticSurface(Stream stream) {"
if marker not in s:
    raise SystemExit("ReFS descriptor insertion marker not found")
methods = '''  /// <summary>
  /// Offline-quiescent existing-file replacement for the proven regular-stream profile.
  /// A new name is rejected before mutation until ReFS file-identity/security/link fields
  /// are proven for every supported 3.x profile.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => RefsOfflineModifier.Add(archive, inputs);

  /// <summary>
  /// Removes regular files or empty directories from an unmounted ReFS image. Namespace
  /// deletion is published through immutable B+ replacement pages and the alternate CHKP.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => RefsOfflineModifier.Remove(archive, entryNames);

'''
s = s.replace(marker, methods + marker, 1)
p.write_text(s, encoding="utf-8")

# The readiness document distinguishes the library's offline image-editor surface
# from full native mounted-driver R/W. Do not erase the latter's remaining work.
p = Path("FileSystems/FileSystem.Refs/DRIVER_READINESS.md")
s = p.read_text(encoding="utf-8")
needle = "- [x] explicit offline-vs-native mutation transaction boundary\n"
if needle not in s:
    raise SystemExit("ReFS readiness insertion point not found")
s = s.replace(
    needle,
    needle
    + "- [x] offline-quiescent existing regular-file replacement with allocator-verified reallocation and old-block release\n"
    + "- [x] offline-quiescent regular-file / empty-directory removal through CoW B+ replacement + alternate CHKP publication\n"
    + "- [x] archive API exposes the proven offline mutation profile without claiming mounted-driver crash semantics\n",
    1,
)
p.write_text(s, encoding="utf-8")

Path(".github/refs-promotion.py").unlink(missing_ok=True)
