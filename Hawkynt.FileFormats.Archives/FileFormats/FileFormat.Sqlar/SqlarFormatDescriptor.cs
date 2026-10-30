#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sqlar;

/// <summary>
/// SQLite Archive — an SQLite 3 database whose <c>sqlar(name, mode, mtime, sz, data)</c> table holds
/// one row per file or directory, the content zlib-compressed whenever that is smaller (then
/// <c>sz</c> differs from the blob's length). It is what <c>sqlite3 -A</c> reads and writes.
///
/// <para>Create writes a fresh database with that table and its primary-key index; edits rebuild the
/// database from its rows, keeping every untouched row's columns (and the stored CREATE statement)
/// exactly. A database holding anything besides the archive table is refused for editing rather than
/// rewritten without it.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.sqlite.org/sqlar.html</c> — SQLite Archive Files</description></item>
///   <item><description><c>https://www.sqlite.org/fileformat2.html</c> — the database file format</description></item>
/// </list>
/// </summary>
public sealed class SqlarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  /// <summary>The CREATE statement written for new archives, as sqlite3's shell defines the table.</summary>
  public const string CreateSql =
    "CREATE TABLE sqlar(\n" +
    "  name TEXT PRIMARY KEY,  -- name of the file\n" +
    "  mode INT,               -- access permissions\n" +
    "  mtime INT,              -- last modification time\n" +
    "  sz INT,                 -- original file size\n" +
    "  data BLOB               -- compressed content\n" +
    ")";

  private const long FileMode = 0x81A4; // regular file, 0644
  private const long DirectoryMode = 0x41ED; // directory, 0755

  public string Id => "Sqlar";
  public string DisplayName => "SQLite Archive";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sqlar";
  public IReadOnlyList<string> Extensions => [".sqlar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // The SQLite header belongs to the Sqlite descriptor; an archive is told apart by its extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "zlib"), new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SQLite Archive (sqlite3 -A): files as rows of an sqlar table, zlib-compressed where smaller.";

  /// <summary>One archive row.</summary>
  public sealed record SqlarEntry(long RowId, string Name, long Mode, long MTime, long Size, byte[]? Data) {
    public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
    public bool IsCompressed => this.Data != null && this.Size >= 0 && this.Size != this.Data.Length;

    /// <summary>The file content, decompressed when it was stored compressed.</summary>
    public byte[] Content() {
      if (this.Data == null) return [];
      if (!this.IsCompressed) return this.Data;
      var raw = FileFormat.Zlib.ZlibStream.Decompress(this.Data);
      if (raw.Length != this.Size)
        throw new InvalidDataException($"sqlar: '{this.Name}' inflates to {raw.Length} bytes, the row says {this.Size}.");
      return raw;
    }

    public DateTime? LastModified => this.MTime > 0 && this.MTime < 253402300800 ? DateTime.UnixEpoch.AddSeconds(this.MTime) : null;
  }

  /// <summary>The database and its archive rows.</summary>
  public static (SqliteDatabase Database, SqliteDatabase.SchemaEntry Table, List<SqlarEntry> Entries) Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var db = new SqliteDatabase(ms.ToArray());
    var table = db.Schema.FirstOrDefault(s => s.Type == "table" && string.Equals(s.Name, "sqlar", StringComparison.OrdinalIgnoreCase))
      ?? throw new InvalidDataException("sqlar: the database has no sqlar table.");
    var entries = db.ReadTable(table.RootPage).Select(r => {
      object? Col(int i) => i < r.Values.Length ? r.Values[i] : null;
      return new SqlarEntry(r.RowId, Col(0) as string ?? throw new InvalidDataException($"sqlar: row {r.RowId} has no name."),
        Col(1) is long mode ? mode : 0, Col(2) is long mtime ? mtime : 0, Col(3) is long sz ? sz : 0,
        Col(4) switch { byte[] b => b, string s => System.Text.Encoding.UTF8.GetBytes(s), _ => null });
    }).ToList();
    return (db, table, entries);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (_, _, entries) = Read(stream);
    return [.. entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.IsDirectory ? 0 : Math.Max(e.Size, e.Data?.Length ?? 0),
      e.Data?.Length ?? 0, e.IsCompressed ? "deflate" : "store", e.IsDirectory, false, e.LastModified))];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (_, _, entries) = Read(stream);
    foreach (var e in entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(SafeJoin(outputDir, e.Name));
        continue;
      }
      WriteFile(outputDir, e.Name, e.Content());
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var rows = new List<SqliteDatabase.Row>();
    foreach (var input in inputs)
      rows.Add(RowFor(rows.Count + 1, input));
    output.Write(Build(CreateSql, rows));
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, rows => {
      var next = rows.Count == 0 ? 1 : rows.Max(r => r.RowId) + 1;
      foreach (var input in inputs) {
        var name = Normalize(input.ArchiveName);
        var existing = rows.FindIndex(r => (string?)r.Values[0] == name);
        if (existing >= 0) rows[existing] = RowFor(rows[existing].RowId, input);
        else rows.Add(RowFor(next++, input));
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = entryNames.Select(Normalize).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, rows => rows.RemoveAll(r => r.Values[0] is string n && drop.Contains(n)));
  }

  private static void Rewrite(Stream archive, Action<List<SqliteDatabase.Row>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    var (db, table, _) = Read(archive);
    var autoIndex = $"sqlite_autoindex_{table.Name}_1";
    var foreign = db.Schema.Where(s => !(s.Type == "table" && s.Name == table.Name)
      && !(s.Type == "index" && s.Name == autoIndex && s.Sql == null)).Select(s => $"{s.Type} {s.Name}").ToList();
    if (foreign.Count > 0)
      throw new NotSupportedException($"sqlar: the database also holds {string.Join(", ", foreign)}; rebuilding it would drop them.");
    if (!db.Schema.Any(s => s.Name == autoIndex))
      throw new NotSupportedException("sqlar: the archive table has no primary-key index; it was not created as an SQLite Archive.");
    var rows = db.ReadTable(table.RootPage);
    if (rows.Any(r => r.Values.Length != 5 || r.Values[0] is not string))
      throw new NotSupportedException("sqlar: a row does not have the five archive columns with a text name.");
    edit(rows);
    var image = Build(table.Sql ?? CreateSql, rows, table.Name);
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(image);
    archive.Flush();
  }

  private static byte[] Build(string sql, IReadOnlyList<SqliteDatabase.Row> rows, string tableName = "sqlar")
    => SqliteDatabaseWriter.Write([new SqliteDatabaseWriter.Table(tableName, sql, rows, AutoIndexColumn: 0)]);

  private static SqliteDatabase.Row RowFor(long rowId, ArchiveInputInfo input) {
    var name = Normalize(input.ArchiveName);
    if (name.Length == 0) throw new ArgumentException("sqlar: an entry needs a name.");
    var mtime = input.Metadata?.LastWriteTimeUtc?.ToUnixTimeSeconds() ?? 0;
    if (input.IsDirectory)
      return new(rowId, [name, DirectoryMode, mtime, 0L, null]);
    var data = input.ReadContent();
    // sqlite3 -A stores the zlib stream only when it is smaller than the content.
    var packed = data.Length == 0 ? data : FileFormat.Zlib.ZlibStream.Compress(data);
    return new(rowId, [name, FileMode, mtime, (long)data.Length, packed.Length < data.Length ? packed : data]);
  }

  private static string Normalize(string name) => name.Replace('\\', '/').Trim('/');

  private static string SafeJoin(string root, string name) {
    var full = Path.GetFullPath(Path.Combine(root, name));
    var rootFull = Path.GetFullPath(root);
    if (!full.StartsWith(rootFull, StringComparison.Ordinal))
      throw new InvalidDataException($"sqlar: '{name}' escapes the output directory.");
    return full;
  }
}
