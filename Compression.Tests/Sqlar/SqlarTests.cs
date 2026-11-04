using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Sqlar;

namespace Compression.Tests.Sqlar;

/// <summary>
/// SQLite Archive read/write. Oracles: the SQLite library itself, through Python's bundled
/// <c>sqlite3</c> module (integrity check plus a row dump of what we write, and the writer of archives
/// we read), and the <c>sqlite3 -A</c> shell where WSL has it.
/// </summary>
[TestFixture]
public class SqlarTests {

  private static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello sqlar\n", 50)));
  private static readonly byte[] Noise = RandomBytes(9000);

  private static byte[] RandomBytes(int length) {
    var bytes = new byte[length];
    new Random(20261030).NextBytes(bytes);
    return bytes;
  }

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs) {
    using var ms = new MemoryStream();
    new SqlarFormatDescriptor().Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  private static ArchiveInputInfo File(string name, byte[] data, long mtime = 1_793_000_000)
    => ArchiveInputInfo.InMemory(name, data, new ArchiveEntryMetadata(LastWriteTimeUtc: DateTimeOffset.FromUnixTimeSeconds(mtime)));

  private static Dictionary<string, byte[]> Contents(byte[] archive) {
    var (_, _, entries) = SqlarFormatDescriptor.Read(new MemoryStream(archive));
    return entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e.Content());
  }

  // ── an archive the sqlite3 shell wrote ────────────────────────────────

  private static byte[] Vector(string name) {
    using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"SqlarVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded sqlar vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_ArchiveWrittenBySqlite3Shell_When_Read_Then_RowsMatchSqlite3() {
    // sqlite3 3.45.1: `sqlite3 cli.sqlar -Ac hello.txt empty.txt sub`; its own `select name,mode,mtime,sz,length(data)`.
    var (_, _, entries) = SqlarFormatDescriptor.Read(new MemoryStream(Vector("sqlite3-cli.sqlar")));
    Assert.That(entries.Select(e => (e.Name, e.Mode, e.MTime, e.Size, e.Data?.Length)), Is.EqualTo(new (string, long, long, long, int?)[] {
      ("hello.txt", 33279, 1793008800, 600, 27), ("empty.txt", 33279, 1793008800, 0, 0),
      ("sub", 16895, 1793008800, 0, null), ("sub/random.bin", 33279, 1793008800, 9000, 1275),
    }));
    var contents = Contents(Vector("sqlite3-cli.sqlar"));
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contents["hello.txt"])),
      Is.EqualTo("9198ded75a1f500b5f60e6fad64769850ac4d4c50cda3287af3a6fb5aff4f1bb"));
    Assert.That(contents["empty.txt"], Is.Empty);
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contents["sub/random.bin"])),
      Is.EqualTo("73b45f6dcbe86e68eb50efb86d0cf8e0435bbad1d1ad9616ea88446ba451e702"));
  }

  // ── own round trip ─────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Given_FilesAndADirectory_When_Created_Then_ReadBackWithModesTimesAndContent() {
    var archive = Create([File("hello.txt", Text), File("sub/noise.bin", Noise), File("empty.txt", []),
      new ArchiveInputInfo("sub", "sub", true)]);
    var (db, table, entries) = SqlarFormatDescriptor.Read(new MemoryStream(archive));
    Assert.That(db.PageSize, Is.EqualTo(4096));
    Assert.That(table.Sql, Is.EqualTo(SqlarFormatDescriptor.CreateSql));
    Assert.That(db.Schema.Select(s => s.Name), Is.EqualTo(new[] { "sqlar", "sqlite_autoindex_sqlar_1" }));
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "hello.txt", "sub/noise.bin", "empty.txt", "sub" }));
    Assert.That(entries[0].IsCompressed, Is.True, "repetitive text is stored deflated");
    Assert.That(entries[1].IsCompressed, Is.False, "incompressible data is stored raw");
    Assert.That(entries[3].IsDirectory, Is.True);
    Assert.That(entries[0].Mode, Is.EqualTo(0x81A4));
    Assert.That(entries[0].MTime, Is.EqualTo(1_793_000_000));
    Assert.That(Contents(archive), Is.EqualTo(new Dictionary<string, byte[]> { ["hello.txt"] = Text, ["sub/noise.bin"] = Noise, ["empty.txt"] = [] }));
  }

  [Test, Category("RoundTrip")]
  public void Given_ManyAndLargeFiles_When_Created_Then_MultiLevelTreesAndOverflowChainsReadBack() {
    var inputs = Enumerable.Range(0, 1500)
      .Select(i => File($"dir{i % 7}/file-{i:D5}-{new string('x', i % 300)}.dat", [.. Enumerable.Range(0, 50 + i % 900).Select(j => (byte)((j * 7919 + i) >> 3))]))
      .Append(File("huge.bin", [.. Enumerable.Range(0, 300_000).Select(j => (byte)((j * 2654435761u) >> 11))]))
      .ToList();
    var archive = Create(inputs);
    var read = Contents(archive);
    Assert.That(read, Has.Count.EqualTo(inputs.Count));
    foreach (var input in inputs)
      Assert.That(read[input.ArchiveName], Is.EqualTo(input.InMemoryContent), input.ArchiveName);
  }

  [Test, Category("RoundTrip")]
  public void Given_Archive_When_FileReplacedAddedAndRemoved_Then_OtherRowsSurviveUnchanged() {
    var d = new SqlarFormatDescriptor();
    using var ms = new MemoryStream(Create([File("a.txt", Text), File("b.bin", Noise), File("c.txt", [1, 2, 3])]));
    d.Add(ms, [File("b.bin", Text, 5), File("d.txt", [9])]);
    d.Remove(ms, ["a.txt"]);
    var (_, _, entries) = SqlarFormatDescriptor.Read(ms);
    Assert.That(entries.Select(e => (e.RowId, e.Name)), Is.EqualTo(new[] { (2L, "b.bin"), (3L, "c.txt"), (4L, "d.txt") }));
    Assert.That(entries[0].Content(), Is.EqualTo(Text));
    Assert.That(entries[0].MTime, Is.EqualTo(5));
    Assert.That(entries[1].MTime, Is.EqualTo(1_793_000_000));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoInputs_When_Created_Then_EmptyArchiveLists() {
    var archive = Create([]);
    Assert.That(new SqlarFormatDescriptor().List(new MemoryStream(archive), null), Is.Empty);
    Assert.That(archive, Has.Length.EqualTo(3 * 4096));
  }

  [Test, Category("EdgeCase")]
  public void Given_DuplicateNames_When_Created_Then_Refused()
    => Assert.Throws<ArgumentException>(() => Create([File("a", [1]), File("a", [2])]));

  [Test, Category("EdgeCase")]
  public void Given_DatabaseWithAnotherTable_When_Edited_Then_RefusedAndUntouched() {
    var image = SqliteDatabaseWriter.Write([
      new SqliteDatabaseWriter.Table("sqlar", SqlarFormatDescriptor.CreateSql, [new(1, ["a", 0x81A4L, 0L, 1L, new byte[] { 7 }])], 0),
      new SqliteDatabaseWriter.Table("notes", "CREATE TABLE notes(x)", [new(1, ["keep me"])]),
    ]);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => new SqlarFormatDescriptor().Remove(ms, ["a"]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("EdgeCase")]
  public void Given_PlainDatabase_When_Listed_Then_InvalidData() {
    var image = SqliteDatabaseWriter.Write([new SqliteDatabaseWriter.Table("t", "CREATE TABLE t(x)", [])]);
    Assert.Throws<InvalidDataException>(() => new SqlarFormatDescriptor().List(new MemoryStream(image), null));
  }

  [Test, Category("EdgeCase")]
  public void Given_RowWhoseDeflatedDataLies_When_Extracted_Then_InvalidData() {
    var lying = FileFormat.Zlib.ZlibStream.Compress(Text);
    var image = SqliteDatabaseWriter.Write([new SqliteDatabaseWriter.Table("sqlar", SqlarFormatDescriptor.CreateSql,
      [new(1, ["a.txt", 0x81A4L, 0L, (long)Text.Length + 1, lying])], 0)]);
    Assert.Throws<InvalidDataException>(() => new SqlarFormatDescriptor().Extract(new MemoryStream(image), Path.GetTempPath(), null, ["a.txt"]));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotSqlite_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new SqliteDatabase(new byte[4096]));

  [TestCase(0L)]
  [TestCase(1L)]
  [TestCase(127L)]
  [TestCase(-1L)]
  [TestCase(8388608L)]
  [TestCase(long.MinValue)]
  [TestCase(long.MaxValue)]
  [Category("EdgeCase")]
  public void Given_IntegerBoundary_When_RecordRoundTrips_Then_Equal(long value) {
    var record = SqliteDatabaseWriter.EncodeRecord([value, "x", null]);
    Assert.That(SqliteDatabase.DecodeRecord(record), Is.EqualTo(new object?[] { value, "x", null }));
  }

  [TestCase(0L)]
  [TestCase(240L)]
  [TestCase(0x7FL)]
  [TestCase(0x80L)]
  [TestCase(0x00FFFFFFFFFFFFFFL)]
  [TestCase(0x0100000000000000L)]
  [TestCase(-1L)]
  [Category("EdgeCase")]
  public void Given_VarintBoundary_When_RoundTripped_Then_Equal(long value) {
    using var ms = new MemoryStream();
    SqliteDatabaseWriter.WriteVarint(ms, value);
    Assert.That(ms.Length, Is.EqualTo(SqliteDatabaseWriter.VarintLength(value)));
    var at = 0;
    Assert.That(SqliteDatabase.ReadVarint(ms.ToArray(), ref at), Is.EqualTo(value));
  }

  // ── SQLite itself as the oracle ────────────────────────────────────────

  private static string? Python() {
    foreach (var name in new[] { "python3", "python" }) {
      try {
        using var p = Process.Start(new ProcessStartInfo(name, "-c \"import sqlite3,zlib;print(sqlite3.sqlite_version)\"") {
          RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        });
        if (p == null) continue;
        p.WaitForExit(20_000);
        if (p.ExitCode == 0) return name;
      } catch (System.ComponentModel.Win32Exception) {
        // not on PATH
      }
    }
    return null;
  }

  private static (int Exit, string Out) RunPython(string script, params string[] args) {
    var python = Python() ?? throw new IgnoreException("Python with its sqlite3 module is not on PATH.");
    var file = Path.Combine(Path.GetTempPath(), "sqlar_" + Guid.NewGuid().ToString("N")[..8] + ".py");
    System.IO.File.WriteAllText(file, script);
    try {
      var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      psi.ArgumentList.Add("-I");
      psi.ArgumentList.Add(file);
      foreach (var a in args) psi.ArgumentList.Add(a);
      using var p = Process.Start(psi)!;
      var stdout = p.StandardOutput.ReadToEnd();
      var stderr = p.StandardError.ReadToEnd();
      p.WaitForExit();
      return (p.ExitCode, stdout + stderr);
    } finally {
      System.IO.File.Delete(file);
    }
  }

  private const string DumpScript = """
    import sqlite3, sys, zlib, hashlib
    con = sqlite3.connect(sys.argv[1])
    print(con.execute('pragma integrity_check').fetchone()[0])
    for name, mode, mtime, sz, data in con.execute('select name, mode, mtime, sz, data from sqlar order by name'):
        if data is not None and sz != len(data):
            data = zlib.decompress(data)
        h = hashlib.sha256(data or b'').hexdigest()
        print(name, oct(mode), mtime, sz, h)
    """;

  [Test, Category("ExternalInterop")]
  public void Given_OurArchive_When_SqliteChecksAndReadsIt_Then_IntegrityOkAndEveryFileMatches() {
    var inputs = Enumerable.Range(0, 800)
      .Select(i => File($"d{i % 5}/f{i:D4}{new string('n', i % 200)}", [.. Enumerable.Range(0, i % 3 == 0 ? 6000 + i : 40 + i).Select(j => (byte)(j * i))]))
      .Append(File("hello.txt", Text)).Append(File("noise.bin", Noise)).Append(File("empty", [])).ToList();
    var path = Path.Combine(Path.GetTempPath(), "ours_" + Guid.NewGuid().ToString("N")[..8] + ".sqlar");
    System.IO.File.WriteAllBytes(path, Create(inputs));
    try {
      var (exit, output) = RunPython(DumpScript, path);
      Assert.That(exit, Is.Zero, output);
      var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      Assert.That(lines[0], Is.EqualTo("ok"));
      var expected = inputs.OrderBy(i => i.ArchiveName, StringComparer.Ordinal)
        .Select(i => $"{i.ArchiveName} 0o100644 1793000000 {i.InMemoryContent!.Length} {Convert.ToHexStringLower(SHA256.HashData(i.InMemoryContent))}");
      Assert.That(lines.Skip(1), Is.EqualTo(expected));
    } finally {
      System.IO.File.Delete(path);
    }
  }

  [Test, Category("ExternalInterop")]
  public void Given_ArchiveWrittenBySqlite_When_ReadAndEdited_Then_ContentMatchesAndSqliteStillAccepts() {
    var path = Path.Combine(Path.GetTempPath(), "theirs_" + Guid.NewGuid().ToString("N")[..8] + ".sqlar");
    var script = """
      import sqlite3, sys, zlib
      con = sqlite3.connect(sys.argv[1])
      con.execute('''CREATE TABLE sqlar(
        name TEXT PRIMARY KEY,  -- name of the file
        mode INT,               -- access permissions
        mtime INT,              -- last modification time
        sz INT,                 -- original file size
        data BLOB               -- compressed content
      )''')
      for i in range(400):
          raw = bytes((j * 31 + i) % 256 for j in range(37 * (i % 50)))
          packed = zlib.compress(raw)
          con.execute('insert into sqlar values(?,?,?,?,?)', ('dir/%04d.bin' % i, 0o100644, 1700000000 + i, len(raw), packed if len(packed) < len(raw) else raw))
      con.execute("insert into sqlar values('dir', ?, 1700000000, 0, NULL)", (0o40755,))
      con.commit()
      con.execute("delete from sqlar where name = 'dir/0007.bin'")
      con.commit()
      con.close()
      """;
    try {
      var (exit, output) = RunPython(script, path);
      Assert.That(exit, Is.Zero, output);
      var bytes = System.IO.File.ReadAllBytes(path);
      var read = Contents(bytes);
      Assert.That(read, Has.Count.EqualTo(399));
      for (var i = 0; i < 400; ++i) {
        if (i == 7) continue;
        var raw = Enumerable.Range(0, 37 * (i % 50)).Select(j => (byte)((j * 31 + i) % 256)).ToArray();
        Assert.That(read[$"dir/{i:D4}.bin"], Is.EqualTo(raw), $"dir/{i:D4}.bin");
      }
      Assert.That(new SqlarFormatDescriptor().List(new MemoryStream(bytes), null).Single(e => e.Name == "dir").IsDirectory, Is.True);

      using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        new SqlarFormatDescriptor().Add(fs, [File("new.txt", Text)]);
      var (exit2, output2) = RunPython(DumpScript, path);
      Assert.That(exit2, Is.Zero, output2);
      Assert.That(output2, Does.StartWith("ok"));
      Assert.That(output2, Does.Contain($"new.txt 0o100644 1793000000 {Text.Length} {Convert.ToHexStringLower(SHA256.HashData(Text))}"));
      Assert.That(output2, Does.Contain("dir/0399.bin"));
    } finally {
      System.IO.File.Delete(path);
    }
  }
}
