#pragma warning disable CS1591
namespace FileFormat.Bkf;

/// <summary>
/// Writes a Microsoft NTBackup (.bkf) medium in Microsoft Tape Format 1.00a: a TAPE DBLK,
/// then one Data Set — SSET, a VOLB for drive <c>C:</c>, a DIRB for every folder (the root
/// first) each followed by the FILE DBLKs of its files — then the ESET, with soft filemarks
/// (SFMB) between, as NTBackup lays a backup file out.
/// </summary>
/// <remarks>
/// <para>File data goes into a STAN stream followed by its CSUM stream; names are Unicode
/// strings, and a name too long for its DBLK goes into an FNAM/PNAM stream. Every DBLK and
/// stream header carries its checksum, every DBLK starts on a Format Logical Block (512 or
/// 1024 bytes) and ends with a SPAD stream.</para>
/// <para>No Media Based Catalog is written (TAPE Media Based Catalog Type 0), which the
/// format allows (§3.3.2.2); readers and NTBackup's own "catalog" pass walk the DBLKs.</para>
/// </remarks>
public static class BkfWriter {
  /// <summary>The Format Logical Block size NTBackup uses.</summary>
  public const int DefaultLogicalBlockSize = 1024;

  /// <summary>A file (<paramref name="Data"/>) or folder at <paramref name="Path"/> ('/'-separated).</summary>
  public readonly record struct Item(string Path, byte[] Data, bool IsDirectory, DateTime? Modified = null);

  /// <summary>Builds a medium with 1024-byte Format Logical Blocks.</summary>
  public static byte[] Build(IEnumerable<Item> items) => Build(items, DefaultLogicalBlockSize);

  /// <summary>Builds a medium with the given Format Logical Block size (512 or 1024, MTF 1.00a §3.4.2).</summary>
  public static byte[] Build(IEnumerable<Item> items, int logicalBlockSize) {
    ArgumentNullException.ThrowIfNull(items);
    if (logicalBlockSize is not (512 or 1024))
      throw new ArgumentOutOfRangeException(nameof(logicalBlockSize), "MTF 1.00a allows Format Logical Blocks of 512 or 1024 bytes.");

    var now = DateTime.Now;
    var tree = new BkfTree();
    foreach (var item in items) {
      var path = BkfTree.Normalize(item.Path);
      if (item.IsDirectory) tree.AddFolder(path, item.Modified);
      else if (path.Length > 0) tree.AddFile(path, item.Data ?? [], item.Modified);
    }

    using var output = new MemoryStream();
    var set = new MtfComposer.DataSet(
      MtfComposer.Sset(now, "Backup"),
      [MtfComposer.Volb(now, "C:"), .. tree.Blocks(now, logicalBlockSize)],
      MtfComposer.Eset(now, Mtf.SsetNormalBit, 1));
    MtfComposer.Write(output, MtfComposer.Tape(now, "Media created " + now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), logicalBlockSize),
      [set], logicalBlockSize, s => s.Data ?? []);
    return output.ToArray();
  }
}

/// <summary>Folders and files to be written as DIRB and FILE DBLKs.</summary>
internal sealed class BkfTree {
  private readonly SortedDictionary<string, (DateTime? Modified, List<(string Name, byte[] Data, DateTime? Modified)> Files)> _folders
    = new(Comparer<string>.Create((a, b) => string.CompareOrdinal(a.Replace('/', '\0'), b.Replace('/', '\0'))));

  public static string Normalize(string path) => (path ?? "").Replace('\\', '/').Trim('/');

  public void AddFolder(string path, DateTime? modified = null) {
    var parts = path.Length == 0 ? [] : path.Split('/');
    for (var i = 0; i <= parts.Length; ++i) {
      var folder = string.Join('/', parts[..i]);
      if (!this._folders.ContainsKey(folder)) this._folders[folder] = (i == parts.Length ? modified : null, []);
    }
  }

  public void AddFile(string path, byte[] data, DateTime? modified) {
    var slash = path.LastIndexOf('/');
    var folder = slash < 0 ? "" : path[..slash];
    var name = path[(slash + 1)..];
    this.AddFolder(folder);
    var files = this._folders[folder].Files;
    files.RemoveAll(f => f.Name == name);
    files.Add((name, data, modified));
  }

  /// <summary>The root DIRB first, then every folder after its parent, each followed by its FILEs.</summary>
  public IEnumerable<MtfBlock> Blocks(DateTime backup, int flb) {
    if (this._folders.Count == 0) this.AddFolder("");
    foreach (var (path, (modified, files)) in this._folders) {
      var hasChildren = files.Count > 0 || this._folders.Keys.Any(k => k.Length > path.Length && (path.Length == 0 || k.StartsWith(path + "/", StringComparison.Ordinal)));
      yield return MtfComposer.Dirb(path, modified, backup, empty: !hasChildren, flb);
      foreach (var (name, data, fileModified) in files)
        yield return MtfComposer.File(name, data, fileModified, backup, flb);
    }
  }
}
