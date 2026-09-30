using Compression.Lib;
using Compression.NativeUI.Navigation;
using Compression.Registry;

namespace Compression.NativeUI.Editing;

/// <summary>A file or folder picked up for copying or moving: the folder it is in, and its name there.</summary>
/// <param name="From">The folder holding it — on disk or inside an archive.</param>
/// <param name="Name">Its name in that folder.</param>
/// <param name="IsFolder">True for a folder, which travels with everything beneath it.</param>
internal sealed record TransferItem(Location From, string Name, bool IsFolder) {
  /// <summary>The item's full entry path when <see cref="From"/> is inside an archive.</summary>
  public string EntryPath => From.ArchiveFolder + Name;

  /// <summary>The item's path on disk when <see cref="From"/> is a host folder.</summary>
  public string HostPath => Path.Combine(From.HostPath, Name);
}

/// <summary>What a transfer did: the names it created in the target, in order.</summary>
internal sealed record TransferResult(IReadOnlyList<string> Created);

/// <summary>
/// Copies or moves files and folders between any two places the shell can show: host folders and
/// folders inside archives, in every combination.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is ever overwritten. A name already taken in the target gets a number instead —
/// "report (2).txt" — which is also what makes pasting into the folder something came from produce
/// a copy rather than an error.
/// </para>
/// <para>
/// A move copies first and removes the sources only once every copy has landed, so a failure part
/// way leaves a duplicate rather than a loss. Two moves are cheaper than that and keep the bytes
/// where they are: within one disk volume it is a plain move, and within one archive it is a
/// <see cref="ArchiveOperations.Rename"/>.
/// </para>
/// </remarks>
internal static class Transfer {
  /// <summary>
  /// Why a transfer of <paramref name="items"/> into <paramref name="target"/> cannot happen, or null
  /// when it can. Asked before any file is touched.
  /// </summary>
  public static string? WhyNot(IReadOnlyList<TransferItem> items, Location target, bool move) {
    if (items.Count == 0) return "Nothing to paste.";

    if (target.IsInArchive) {
      if (!File.Exists(target.HostPath)) return $"No longer exists: {target.HostPath}";
      if (!IsModifiable(target.HostPath)) return $"{Path.GetFileName(target.HostPath)} cannot be modified.";
    } else if (!Directory.Exists(target.HostPath)) {
      return $"No longer exists: {target.HostPath}";
    }

    foreach (var item in items) {
      if (item.IsFolder && Contains(item, target))
        return $"{item.Name} cannot be put inside itself.";
      if (move && item.From.IsInArchive && !IsModifiable(item.From.HostPath))
        return $"{item.Name} cannot be moved out of {Path.GetFileName(item.From.HostPath)}, which cannot be modified.";
    }

    return null;
  }

  /// <summary>Copies, or with <paramref name="move"/> moves, <paramref name="items"/> into <paramref name="target"/>.</summary>
  /// <exception cref="InvalidOperationException">The transfer is refused; see <see cref="WhyNot"/>.</exception>
  public static TransferResult Run(IReadOnlyList<TransferItem> items, Location target, bool move) {
    if (WhyNot(items, target, move) is { } refusal) throw new InvalidOperationException(refusal);

    // Moving onto the folder something is already in changes nothing.
    var moving = move ? [.. items.Where(i => i.From != target)] : items;
    if (moving.Count == 0) return new([]);

    return target.IsInArchive
      ? IntoArchive(moving, target, move)
      : IntoFolder(moving, target.HostPath, move);
  }

  // ── into a host folder ──────────────────────────────────────────────────────────────────────

  private static TransferResult IntoFolder(IReadOnlyList<TransferItem> items, string folder, bool move) {
    var created = new List<string>();
    var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

    // Everything written here is written beside its destination, under a staging name, and renamed
    // into place once complete: an interrupted transfer never leaves a truncated file under the real
    // name, and nothing is first unpacked onto some other volume only to be copied across.
    using var staging = new Staging(folder);
    foreach (var item in items) {
      var name = FreeName(item.Name, taken);
      var target = Path.Combine(folder, name);

      if (!item.From.IsInArchive && move && SameVolume(item.HostPath, target)) {
        MoveOnDisk(item.HostPath, target, item.IsFolder);
      } else if (item.From.IsInArchive) {
        MoveOnDisk(staging.Materialize(item), target, item.IsFolder);
      } else {
        var partial = staging.NewPath();
        CopyOnDisk(item.HostPath, partial, item.IsFolder);
        MoveOnDisk(partial, target, item.IsFolder);
      }

      created.Add(name);
    }

    if (move) RemoveSources(items.Where(i => i.From.IsInArchive || Exists(i.HostPath, i.IsFolder)));
    return new(created);
  }

  // ── into an archive ─────────────────────────────────────────────────────────────────────────

  private static TransferResult IntoArchive(IReadOnlyList<TransferItem> items, Location target, bool move) {
    var archive = target.HostPath;
    var folder = target.ArchiveFolder ?? "";
    var created = new List<string>();
    var taken = new HashSet<string>(ChildNames(archive, folder), StringComparer.OrdinalIgnoreCase);

    // Within one archive a move is a rename: nothing is extracted and nothing re-added.
    var sameArchive = move
      ? items.Where(i => i.From.IsInArchive && string.Equals(i.From.HostPath, archive, StringComparison.OrdinalIgnoreCase)).ToList()
      : [];
    if (sameArchive.Count > 0) {
      var renames = new List<ArchiveRename>();
      foreach (var item in sameArchive) {
        var name = FreeName(item.Name, taken);
        renames.Add(new(item.EntryPath, folder + name));
        created.Add(name);
      }

      ArchiveOperations.Rename(archive, renames);
    }

    var rest = items.Except(sameArchive).ToList();
    if (rest.Count == 0) return new(created);

    // Entries on their way into the archive are staged beside it, on the volume it is written to.
    using var staging = new Staging(Path.GetDirectoryName(Path.GetFullPath(archive))!);
    var inputs = new List<ArchiveInput>();
    foreach (var item in rest) {
      var name = FreeName(item.Name, taken);
      var source = item.From.IsInArchive ? staging.Materialize(item) : item.HostPath;
      if (item.IsFolder) CollectFolder(source, folder + name, inputs);
      else inputs.Add(new ArchiveInput(source, folder + name));
      created.Add(name);
    }

    ArchiveOperations.Add(archive, inputs);
    if (move) RemoveSources(rest);
    return new(created);
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// <paramref name="name"/> if it is free, else the first free "stem (n).ext", which is then taken.
  /// </summary>
  internal static string FreeName(string name, ISet<string> taken) {
    var candidate = name;
    var extension = Path.GetExtension(name);
    var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
    if (stem.Length == name.Length) extension = "";

    for (var n = 2; taken.Contains(candidate); ++n)
      candidate = $"{stem} ({n}){extension}";

    taken.Add(candidate);
    return candidate;
  }

  /// <summary>Whether <paramref name="target"/> is <paramref name="folder"/> itself or somewhere beneath it.</summary>
  private static bool Contains(TransferItem folder, Location target) {
    if (folder.From.IsInArchive) {
      if (!target.IsInArchive || !string.Equals(folder.From.HostPath, target.HostPath, StringComparison.OrdinalIgnoreCase))
        return false;
      return (target.ArchiveFolder ?? "").StartsWith(folder.EntryPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    var inside = Path.TrimEndingDirectorySeparator(folder.HostPath);
    var where = Path.TrimEndingDirectorySeparator(target.HostPath);
    return string.Equals(where, inside, StringComparison.OrdinalIgnoreCase)
      || where.StartsWith(inside + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsModifiable(string archivePath)
    => DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath, selectedCount: 1) == DeleteMode.ModifiableArchive;

  /// <summary>The names directly inside <paramref name="folder"/> of an archive, implied folders included.</summary>
  private static IEnumerable<string> ChildNames(string archive, string folder) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in ArchiveOperations.List(archive, password: null)) {
      var path = entry.Name.Replace('\\', '/');
      if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;

      var rest = path[folder.Length..].TrimStart('/');
      var slash = rest.IndexOf('/');
      var child = slash >= 0 ? rest[..slash] : rest;
      if (child.Length > 0) names.Add(child);
    }

    return names;
  }

  private static void CollectFolder(string source, string entryFolder, List<ArchiveInput> sink) {
    sink.Add(new ArchiveInput("", entryFolder + "/"));
    foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
      sink.Add(new ArchiveInput("", entryFolder + "/" + Path.GetRelativePath(source, dir).Replace('\\', '/') + "/"));
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
      sink.Add(new ArchiveInput(file, entryFolder + "/" + Path.GetRelativePath(source, file).Replace('\\', '/')));
  }

  private static void RemoveSources(IEnumerable<TransferItem> items) {
    foreach (var group in items.GroupBy(i => i.From.IsInArchive ? i.From.HostPath : null)) {
      if (group.Key is { } archive) {
        // A folder goes with everything under it; Remove takes the folder name and deletes the lot.
        ArchiveOperations.Remove(archive, [.. group.Select(i => i.IsFolder ? i.EntryPath + "/" : i.EntryPath)
          .Concat(group.Where(i => i.IsFolder).SelectMany(i => Descendants(archive, i.EntryPath)))]);
        continue;
      }

      foreach (var item in group)
        if (item.IsFolder) Directory.Delete(item.HostPath, recursive: true);
        else File.Delete(item.HostPath);
    }
  }

  /// <summary>Every entry beneath a folder of an archive — native removers match entry names, not folders.</summary>
  private static IEnumerable<string> Descendants(string archive, string folder) {
    var prefix = folder.TrimEnd('/') + "/";
    return ArchiveOperations.List(archive, password: null)
      .Select(e => e.Name.Replace('\\', '/'))
      .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length);
  }

  private static bool Exists(string path, bool folder) => folder ? Directory.Exists(path) : File.Exists(path);

  private static bool SameVolume(string a, string b)
    => string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

  private static void MoveOnDisk(string source, string target, bool folder) {
    if (folder) Directory.Move(source, target);
    else File.Move(source, target);
  }

  private static void CopyOnDisk(string source, string target, bool folder) {
    if (!folder) {
      File.Copy(source, target);
      return;
    }

    Directory.CreateDirectory(target);
    foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
      Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
      File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
  }

  /// <summary>The prefix of the hidden staging folders a transfer creates beside its destination.</summary>
  internal const string StagingPrefix = ".cwb-transfer-";

  /// <summary>Raised with each staging folder as it is created; lets tests see where staging happens.</summary>
  internal static event Action<string>? StagingCreated;

  /// <summary>
  /// A hidden scratch folder beside a transfer's destination, on the same volume, so that what is
  /// staged there reaches its final name by a rename rather than a second copy.
  /// </summary>
  private sealed class Staging(string beside) : IDisposable {
    private readonly string _root = Path.Combine(beside, StagingPrefix + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A fresh, unused path inside the staging folder, created on first use.</summary>
    public string NewPath() {
      this.EnsureRoot();
      return Path.Combine(this._root, Guid.NewGuid().ToString("N")[..8]);
    }

    private void EnsureRoot() {
      if (Directory.Exists(this._root)) return;

      var info = Directory.CreateDirectory(this._root);
      if (OperatingSystem.IsWindows()) info.Attributes |= FileAttributes.Hidden;
      StagingCreated?.Invoke(this._root);
    }

    /// <summary>Extracts <paramref name="item"/> — a folder with everything beneath it — and returns where it landed.</summary>
    public string Materialize(TransferItem item) {
      var archive = item.From.HostPath;
      var entries = item.IsFolder ? [.. Descendants(archive, item.EntryPath)] : new[] { item.EntryPath };
      var into = this.NewPath();
      Directory.CreateDirectory(into);
      ArchiveOperations.Extract(archive, into, password: null, files: [.. entries.Where(e => !e.EndsWith('/'))]);

      var landed = Path.Combine(into, item.EntryPath.Replace('/', Path.DirectorySeparatorChar));
      if (item.IsFolder) Directory.CreateDirectory(landed);
      else if (!File.Exists(landed)) throw new IOException($"{item.Name} could not be read from {Path.GetFileName(archive)}.");
      return landed;
    }

    public void Dispose() {
      try {
        if (Directory.Exists(this._root)) Directory.Delete(this._root, recursive: true);
      } catch (IOException) {
      } catch (UnauthorizedAccessException) {
      }
    }
  }
}
