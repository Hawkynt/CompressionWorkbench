from pathlib import Path
import re


def text(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, value: str) -> None:
    Path(path).write_text(value, encoding="utf-8")


def sub(path: str, pattern: str, replacement: str, count: int = 1, flags: int = 0) -> None:
    value = text(path)
    new, n = re.subn(pattern, replacement, value, count=count, flags=flags)
    if n != count:
        raise SystemExit(f"{path}: expected {count} replacement(s), got {n}: {pattern}")
    write(path, new)


# CramFS: native mount is read-only; the workbench already has a verified
# extract/edit/re-create modifier, so expose that existing-image edit.
p = "FileSystems/FileSystem.CramFs/CramFsFormatDescriptor.cs"
sub(
    p,
    r"  // WORM \(Write-Once-Read-Many\), NOT R/W:.*?  public FormatCapabilities Capabilities =>\n    FormatCapabilities\.CanList \| FormatCapabilities\.CanExtract \| FormatCapabilities\.CanCreate \|\n    FormatCapabilities\.CanTest \| FormatCapabilities\.SupportsMultipleEntries \| FormatCapabilities\.SupportsDirectories;",
    "  // The on-disk filesystem is read-only when mounted, but CompressionWorkbench\n"
    "  // supports existing-image add/replace/remove by verified relayout/rebuild.\n"
    "  public FormatCapabilities Capabilities =>\n"
    "    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |\n"
    "    FormatCapabilities.CanModify | FormatCapabilities.CanTest |\n"
    "    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;",
    flags=re.S,
)
sub(
    p,
    r'public string Description => "Linux compressed ROM filesystem";',
    'public string Description => "Linux compressed ROM filesystem; offline image mutation is rebuild-backed.";',
)

# SquashFS: same distinction — read-only mount format, editable image.
p = "FileSystems/FileSystem.SquashFs/SquashFsFormatDescriptor.cs"
sub(
    p,
    r"  // WORM \(Write-Once-Read-Many\), NOT R/W:.*?  public FormatCapabilities Capabilities =>\n    FormatCapabilities\.CanList \| FormatCapabilities\.CanExtract \| FormatCapabilities\.CanCreate \|\n    FormatCapabilities\.CanTest \| FormatCapabilities\.SupportsMultipleEntries \| FormatCapabilities\.SupportsDirectories;",
    "  // SquashFS is read-only when mounted, but the workbench can edit an existing\n"
    "  // image by a verified extract/edit/re-create pass. That is R/W at this API.\n"
    "  public FormatCapabilities Capabilities =>\n"
    "    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |\n"
    "    FormatCapabilities.CanModify | FormatCapabilities.CanTest |\n"
    "    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;",
    flags=re.S,
)
sub(
    p,
    r'public string Description => "Linux compressed read-only filesystem";',
    'public string Description => "Linux compressed read-only-on-mount filesystem; offline image mutation is rebuild-backed.";',
)

# EROFS: promote the fully decoded writer/reader profile. Explicit Add/Remove use
# ReadEntries, which throws on an unsupported compressed inode and therefore never
# feeds the descriptor's user-facing placeholder into a rebuilt image.
p = "FileSystems/FileSystem.Erofs/ErofsFormatDescriptor.cs"
sub(p, r"the round-trippable WORM subset", "the round-trippable offline R/W subset")
sub(
    p,
    r"FormatCapabilities\.CanList \| FormatCapabilities\.CanExtract \| FormatCapabilities\.CanCreate \|\n    FormatCapabilities\.CanTest \|",
    "FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |\n"
    "    FormatCapabilities.CanModify | FormatCapabilities.CanTest |",
)
sub(
    p,
    r'public string Description => "Android read-only compressed filesystem; uncompressed \+ inline inode layouts\.";',
    'public string Description => "Android read-only-on-mount filesystem; supported uncompressed/inline profile is offline R/W.";',
)
marker = "  private static ErofsReader OpenReader(Stream stream) {"
value = text(p)
if marker not in value:
    raise SystemExit("EROFS insertion marker missing")
methods = '''  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    archive.Position = 0;
    var label = OpenReader(archive).VolumeName;
    archive.Position = 0;
    ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => ReadEntries(stream),
      buildImage: files => {
        var writer = new ErofsWriter { VolumeName = label };
        foreach (var (name, data) in files) writer.AddFile(name, data);
        return writer.Build();
      });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    archive.Position = 0;
    var label = OpenReader(archive).VolumeName;
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => ReadEntries(stream),
      buildImage: files => {
        var writer = new ErofsWriter { VolumeName = label };
        foreach (var (name, data) in files) writer.AddFile(name, data);
        return writer.Build();
      });
  }

'''
value = value.replace(marker, methods + marker, 1)
write(p, value)

# MSA already has functional Add/Remove against its decoded GEMDOS volume.
p = "FileSystems/FileSystem.Msa/MsaFormatDescriptor.cs"
sub(
    p,
    r"  // WORM, not R/W:.*?  public FormatCapabilities Capabilities =>\n    FormatCapabilities\.CanList \| FormatCapabilities\.CanExtract \| FormatCapabilities\.CanCreate \|\n    FormatCapabilities\.CanTest;",
    "  // Existing MSA images are editable through the decoded GEMDOS volume and\n"
    "  // then re-encoded; physical rebuild does not make the public operation WORM.\n"
    "  public FormatCapabilities Capabilities =>\n"
    "    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |\n"
    "    FormatCapabilities.CanModify | FormatCapabilities.CanTest;",
    flags=re.S,
)

# PFS0 has a real existing-container editor; its capability flag was simply stale.
p = "FileFormats/FileFormat.Pfs0/Pfs0FormatDescriptor.cs"
sub(
    p,
    r"  // WORM, not R/W:.*?  public FormatCapabilities Capabilities =>\n    FormatCapabilities\.CanList \| FormatCapabilities\.CanExtract \| FormatCapabilities\.CanCreate \|\n    FormatCapabilities\.CanTest \| FormatCapabilities\.SupportsMultipleEntries;",
    "  // Existing PFS0 archives support add/replace/remove through Pfs0InPlaceModifier.\n"
    "  public FormatCapabilities Capabilities =>\n"
    "    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |\n"
    "    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;",
    flags=re.S,
)

# Archive-model docs must agree with FormatCapabilities and the package README.
p = "docs/ARCHIVE-MODEL.md"
sub(
    p,
    r"`CanModify` is \*\*withheld\*\* only from \*\*read-only-by-design\*\* formats \(CramFS, SquashFS\) and\n\*\*create-only\*\* formats \(e\.g\. the checksum-record archives Sqx/Wim/Swm/Ace\) — they may still\nback the verbs with a rebuild for convenience, but they do not present themselves as editable\.",
    "`CanModify` is withheld from **create-only** formats: a fresh instance can be written, but no supported edit of an existing instance exists. Read-only-on-mount filesystem formats such as CramFS, SquashFS and EROFS may still advertise `CanModify` when the workbench implements a verified offline edit/rebuild path; the native mount policy and the image-editor API are different concerns.",
)

# EWF: expose the acquired medium as the one semantic mutable entry.
p = "FileFormats/FileFormat.Ewf/EwfFormatDescriptor.cs"
sub(
    p,
    r"public sealed class EwfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable \{",
    "public sealed class EwfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {",
)
sub(
    p,
    r"FormatCapabilities\.CanList \| FormatCapabilities\.CanExtract \| FormatCapabilities\.CanTest \|\n    FormatCapabilities\.CanCreate \| FormatCapabilities\.SupportsMultipleEntries;",
    "FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |\n"
    "    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;",
)
sub(
    p,
    r"var entries = new List<\(string, byte\[\], string\)> \{\n      \(\"metadata\.ini\", BuildMetadata\(img\), \"stored\"\),\n    \};",
    'var entries = new List<(string, byte[], string)> {\n      ("metadata.ini", BuildMetadata(img), "stored"),\n    };\n    if (EwfMedia.TryExtract(img, out var medium))\n      entries.Add(("media.raw", medium, "stored"));',
)
marker = "  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {"
value = text(p)
if marker not in value:
    raise SystemExit("EWF insertion marker missing")
methods = '''  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory).ToList();
    if (files.Count != 1)
      throw new ArgumentException("EWF mutation accepts exactly one replacement medium.", nameof(inputs));
    var media = files[0].ReadContent();
    var rebuilt = new EwfWriter().Build(media);
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(rebuilt);
    archive.Position = 0;
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (!entryNames.Any(n => string.Equals(n, "media.raw", StringComparison.OrdinalIgnoreCase)))
      throw new NotSupportedException("EWF diagnostic sections are derived metadata and cannot be removed independently; remove media.raw to clear the acquired medium.");
    var rebuilt = new EwfWriter().Build([]);
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(rebuilt);
    archive.Position = 0;
  }

'''
value = value.replace(marker, methods + marker, 1)
write(p, value)

# Delete the one-shot machinery from the final branch.
Path(".github/workflows/rw-promotion-once.yml").unlink(missing_ok=True)
Path(".github/rw-promotion.py").unlink(missing_ok=True)
