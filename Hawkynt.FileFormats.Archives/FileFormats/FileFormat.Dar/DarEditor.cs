using System.Globalization;
using System.Text.RegularExpressions;
using Compression.Registry;

namespace FileFormat.Dar;

/// <summary>
/// Edits a DAR archive in place (<c>docs/DAR-ON-DISK.md</c>, section 13): the archive is read and
/// every kept file checked against its checksum, then written again in the same format settings —
/// compression, block size, tape marks, slice sizes, encryption with its key derivation — with the
/// kept entries carried over byte for byte (inode bytes, stored data, compression letter, data
/// flags, checksum) and the added files written as new. The rewrite goes to temporary files first;
/// only a complete archive replaces the old one, so a refused or failed edit leaves it untouched.
/// </summary>
internal static partial class DarEditor {
  [GeneratedRegex(@"^(?<base>.+)\.(?<n>[0-9]+)\.dar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex SliceName();

  /// <summary>Adds <paramref name="add"/> (replacing files of the same path) and removes <paramref name="remove"/> (a directory with everything below it).</summary>
  /// <exception cref="NotSupportedException">The archive holds what the writer cannot carry over (see section 13).</exception>
  /// <exception cref="ArchivePasswordException">The archive is encrypted and the password is missing or wrong.</exception>
  public static void Edit(Stream archive, IReadOnlyList<ArchiveInputInfo> add, IReadOnlyList<string> remove, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    var slicePaths = SlicePaths(archive);
    var work = Directory.CreateTempSubdirectory("cwb-dar-edit-");
    try {
      int written;
      using (var set = DarSliceSet.Open(archive)) {
        var source = DarArchive.Read(set.Archive, password);
        var (items, settings) = Plan(source, set, add, remove, password);
        if (slicePaths is null && settings.SliceSize > 0)
          throw new NotSupportedException("A DAR slice set is edited through one of its slice files (basename.N.dar).");
        written = 0;
        // the writer disposes every slice but the first, which stays with the caller
        Stream? first = null;
        try {
          DarWriter.Write(n => {
            written = Math.Max(written, n);
            var slice = File.Create(Path.Combine(work.FullName, $"edit.{n.ToString(CultureInfo.InvariantCulture)}.dar"));
            if (n == 1)
              first = slice;
            return slice;
          }, items, settings);
        } finally {
          first?.Dispose();
        }
        if (slicePaths is { } paths) {
          var opened = paths.FindIndex(p => SamePath(p, ((FileStream)archive).Name)) + 1;
          if (opened > written)
            throw new NotSupportedException($"The edited DAR archive has {written} slice(s), so the opened slice {opened} would be deleted; open slice 1 to edit the set.");
        }
      }
      // Everything is written and the old archive is closed: replace it slice by slice.
      for (var n = 1; n <= written; ++n) {
        var made = Path.Combine(work.FullName, $"edit.{n.ToString(CultureInfo.InvariantCulture)}.dar");
        var target = slicePaths is null ? null : SlicePath(slicePaths, n);
        if (target is null || SamePath(target, ((FileStream)archive).Name)) {
          using var input = File.OpenRead(made);
          archive.Position = 0;
          archive.SetLength(0);
          input.CopyTo(archive);
          archive.Flush();
        } else
          File.Copy(made, target, overwrite: true);
      }
      if (slicePaths is not null)
        for (var n = written + 1; n <= slicePaths.Count; ++n)
          File.Delete(slicePaths[n - 1]);
    } finally {
      foreach (var f in work.EnumerateFiles())
        f.Delete();
      work.Delete();
    }
  }

  /// <summary>The slice files of the set <paramref name="archive"/> is a slice of, or null for an unnamed or unsliced stream.</summary>
  private static List<string>? SlicePaths(Stream archive) {
    if (archive is not FileStream { Name: { Length: > 0 } name })
      return null;
    var match = SliceName().Match(Path.GetFileName(name));
    if (!match.Success)
      return null;
    var directory = Path.GetDirectoryName(Path.GetFullPath(name))!;
    var extension = Path.GetFileName(name)[^4..];
    var paths = new List<string>();
    for (var n = 1; ; ++n) {
      var path = Path.Combine(directory, $"{match.Groups["base"].Value}.{n.ToString(CultureInfo.InvariantCulture)}{extension}");
      if (!File.Exists(path))
        break;
      paths.Add(path);
    }
    return paths.Count > 0 ? paths : null;
  }

  private static string SlicePath(List<string> paths, int n) {
    if (n <= paths.Count)
      return paths[n - 1];
    var match = SliceName().Match(Path.GetFileName(paths[0]));
    return Path.Combine(Path.GetDirectoryName(paths[0])!, $"{match.Groups["base"].Value}.{n.ToString(CultureInfo.InvariantCulture)}{Path.GetFileName(paths[0])[^4..]}");
  }

  private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

  /// <summary>What to write: the kept entries in catalogue order, then the added ones, under the source's settings.</summary>
  internal static (List<DarWriter.Item> Items, DarWriter.Settings Settings) Plan(DarArchive source, DarSliceSet set,
      IReadOnlyList<ArchiveInputInfo> add, IReadOnlyList<string> remove, string? password) {
    var trailer = source.Trailer;
    if (!trailer.AtLeast(9, 0))
      throw new NotSupportedException($"DAR archive format {trailer.Major}.{trailer.Minor} is not edited; formats 9 and later are.");
    if (trailer.Algorithm is not ('n' or 'z' or 'y' or 'x' or 'd' or 'q'))
      throw new NotSupportedException($"A DAR archive compressed with {DarArchive.AlgorithmName(trailer.Algorithm)} is not edited: its catalogue could not be written back.");

    static string Clean(string path) => path.Replace('\\', '/').Trim('/');
    var gone = remove.Select(Clean).Where(p => p.Length > 0).ToList();
    var replaced = add.Where(i => !i.IsDirectory).Select(i => Clean(i.ArchiveName)).ToHashSet(StringComparer.Ordinal);
    bool Dropped(DarEntry e) => gone.Any(r => e.Path == r || e.Path.StartsWith(r + "/", StringComparison.Ordinal))
      || e.Kind != DarEntryKind.Directory && replaced.Contains(e.Path);

    var items = new List<DarWriter.Item>();
    foreach (var e in source.Entries) {
      if (Dropped(e))
        continue;
      items.Add(Keep(source, e));
    }
    items.AddRange(add.Select(DarFormatDescriptor.ToItem));

    var header = set.Headers[0];
    var settings = new DarWriter.Settings(trailer.Algorithm, null, trailer.HasTapeMarks,
      (long)(header.SliceSize ?? 0), (long)(header.FirstSliceSize ?? 0), (int)trailer.BlockSize) {
      InPlace = source.InPlace,
      RootInode = source.RootInode,
      FixedBlockSize = true,
      Encryption = trailer.IsEncrypted
        ? new DarWriter.Encryption(trailer.Cipher, password!, trailer.Kdf?.Hash ?? 'a', trailer.Kdf?.Iterations ?? 10000)
        : null,
    };
    return (items, settings);
  }

  /// <summary>
  /// A kept entry, after its data passed its checksum; refused when it carries what the writer
  /// cannot reproduce exactly.
  /// </summary>
  private static DarWriter.Item Keep(DarArchive source, DarEntry e) {
    string? why = null;
    if (e.Status != DarSavedStatus.Saved)
      why = $"its data is not in this archive ({e.Status})";
    else if (e.HardLinkLabel is not null)
      why = "it is a hard link";
    else if (e.HasExtendedAttributes)
      why = "it has extended attributes";
    else if (e.HasFilesystemAttributes)
      why = "it has file-system specific attributes";
    else if (e.Kind is not (DarEntryKind.Directory or DarEntryKind.File or DarEntryKind.Symlink))
      why = $"it is a {e.Kind}";
    else if ((e.RawDataFlags & DarArchive.DataDeltaSignature) != 0)
      why = "it carries a delta signature";
    if (why != null)
      throw new NotSupportedException($"The DAR archive is not edited: '{e.Path}' cannot be carried over, {why}.");

    var kind = e.Kind switch {
      DarEntryKind.Directory => DarWriter.ItemKind.Directory,
      DarEntryKind.Symlink => DarWriter.ItemKind.Symlink,
      _ => DarWriter.ItemKind.File,
    };
    var item = new DarWriter.Item(e.Path, kind, e.Permissions, e.LastModified ?? DateTime.UnixEpoch, e.LastAccess ?? DateTime.UnixEpoch) {
      Uid = e.Uid,
      Gid = e.Gid,
      Changed = e.LastChange,
      LinkTarget = e.LinkTarget,
    };
    if (kind != DarWriter.ItemKind.File)
      return item with { Kept = new DarWriter.Kept(e.RawInode) };
    // verify before carrying over: a damaged file must not travel on under its old checksum
    source.CopyData(e, Stream.Null);
    return item with {
      Length = (long)e.Size,
      Kept = new DarWriter.Kept(e.RawInode) {
        OpenStored = () => source.OpenStored(e),
        Size = e.Size,
        StorageSize = e.StorageSize,
        DataFlags = e.RawDataFlags,
        Algorithm = e.Algorithm,
        Checksum = e.Checksum,
      },
    };
  }
}
