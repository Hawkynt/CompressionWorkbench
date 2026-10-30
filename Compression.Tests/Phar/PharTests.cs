using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Phar;

namespace Compression.Tests.Phar;

/// <summary>
/// PHP archives. Oracle: PHP 8.4's Phar extension. The plain-named fixtures come from php-src's own
/// phar test files; the <c>php-*.phar</c> ones PHP wrote for this suite. <c>php-listing.txt</c> is
/// PHP's view of every fixture (see <see cref="ListingScript"/>). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class PharTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PharVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded PHAR vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly byte[] ATxt = "hello phar\n"u8.ToArray();
  private static readonly byte[] BBin = [.. Enumerable.Range(0, 1024).Select(i => (byte)i)];

  /// <summary>
  /// PHP's listing: <c># file sig=TYPE:hex alias=… meta=base64</c>, then per file
  /// <c>file name size csize crc32 perms method sha256 meta</c> and per directory <c>file name/ dir</c>.
  /// </summary>
  private const string ListingScript = """
    <?php
    foreach (array_slice($argv, 1) as $path) {
      $base = basename($path);
      try { $p = new Phar($path); } catch (Throwable $e) { echo "# $base error=" . get_class($e) . "\n"; continue; }
      $sig = $p->getSignature();
      $alias = $p->getAlias();
      $meta = $p->hasMetadata() ? base64_encode(serialize($p->getMetadata())) : '-';
      echo "# $base sig=" . ($sig ? str_replace(' ', '_', $sig['hash_type']) . ':' . strtolower($sig['hash']) : '-') .
           " alias=" . ($alias === null || $alias === '' || str_contains($alias, "/") ? '-' : $alias) . " meta=$meta\n";
      $rows = [];
      foreach (new RecursiveIteratorIterator($p, RecursiveIteratorIterator::SELF_FIRST) as $file) {
        $name = str_replace('\\', '/', substr($file->getPathname(), strlen('phar://' . $p->getPath() . '/')));
        if ($file->isDir()) { $rows[$name] = "$base $name/ dir"; continue; }
        $c = $file->isCompressed(Phar::GZ) ? 'gz' : ($file->isCompressed(Phar::BZ2) ? 'bz2' : 'stored');
        $m = $file->hasMetadata() ? base64_encode(serialize($file->getMetadata())) : '-';
        $rows[$name] = sprintf("%s %s %d %d %08x %o %s %s %s", $base, $name, $file->getSize(), $file->getCompressedSize(),
          $file->getCRC32() & 0xFFFFFFFF, $file->getPerms() & 0777, $c, hash('sha256', file_get_contents($file->getPathname())), $m);
      }
      ksort($rows, SORT_STRING);
      foreach ($rows as $r) echo "$r\n";
    }
    """;

  private static List<string[]> Listing(string text, string vector)
    => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' '))
      .Where(l => l[0] == vector || (l[0] == "#" && l[1] == vector))
      .ToList();

  private static void AssertMatchesPhp(byte[] bytes, List<string[]> lines) {
    var header = lines.Single(l => l[0] == "#");
    Assert.That(header[2], Does.StartWith("sig="), "PHP could not open the archive: " + string.Join(' ', header));
    var r = new PharReader(new MemoryStream(bytes));

    var sig = header[2]["sig=".Length..];
    var expectedType = sig[..sig.IndexOf(':')] switch {
      "MD5" => PharSignatureType.Md5, "SHA-1" => PharSignatureType.Sha1, "SHA-256" => PharSignatureType.Sha256,
      "SHA-512" => PharSignatureType.Sha512, "OpenSSL" => PharSignatureType.OpenSsl, var t => throw new AssertionException(t),
    };
    Assert.That(r.SignatureType, Is.EqualTo(expectedType));
    Assert.That(Convert.ToHexStringLower(r.Signature), Is.EqualTo(sig[(sig.IndexOf(':') + 1)..]));
    var alias = header[3]["alias=".Length..];
    if (alias != "-") Assert.That(r.Alias, Is.EqualTo(alias));
    var meta = header[4]["meta=".Length..];
    Assert.That(r.Metadata.Length == 0 ? "-" : Convert.ToBase64String(r.Metadata), Is.EqualTo(meta));

    var files = lines.Where(l => l[0] != "#" && l[2] != "dir").ToList();
    var ours = r.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    Assert.That(ours.Select(e => e.Name), Is.EqualTo(files.Select(l => l[1])));
    for (var i = 0; i < ours.Count; ++i) {
      var e = ours[i];
      var l = files[i];
      Assert.That(e.UncompressedSize, Is.EqualTo(uint.Parse(l[2])), e.Name);
      Assert.That(e.CompressedSize, Is.EqualTo(uint.Parse(l[3])), e.Name);
      Assert.That(e.Crc32.ToString("x8"), Is.EqualTo(l[4]), e.Name);
      Assert.That(Convert.ToString(e.Permissions, 8), Is.EqualTo(l[5]), e.Name);
      Assert.That(e.Compression switch { PharCompression.Gzip => "gz", PharCompression.Bzip2 => "bz2", _ => "stored" }, Is.EqualTo(l[6]), e.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.Extract(e))), Is.EqualTo(l[7]), e.Name);
      Assert.That(e.Metadata.Length == 0 ? "-" : Convert.ToBase64String(e.Metadata), Is.EqualTo(l[8]), e.Name);
    }
    var phpDirs = lines.Where(l => l[0] != "#" && l[2] == "dir").Select(l => l[1]).ToHashSet();
    foreach (var d in r.Entries.Where(e => e.IsDirectory))
      Assert.That(phpDirs, Does.Contain(d.Name), "explicit directory PHP does not list");
  }

  [TestCase("md5.phar")]
  [TestCase("sha1.phar")]
  [TestCase("sha256.phar")]
  [TestCase("sha512.phar")]
  [TestCase("openssl.phar")]
  [TestCase("blog.phar")]
  [TestCase("stuboflength1041.phar")]
  [TestCase("gh20442.phar")]
  [TestCase("php-sha256.phar")]
  [TestCase("php-gz-sha1.phar")]
  [TestCase("php-md5.phar")]
  [TestCase("php-sha512-gz.phar")]
  [TestCase("php-bz2-sha256.phar")]
  [Category("ThirdPartyVector")]
  public void Given_PhpFixture_When_Read_Then_MatchesPhpListing(string vector)
    => AssertMatchesPhp(Vector(vector), Listing(Encoding.ASCII.GetString(Vector("php-listing.txt")), vector));

  [TestCase("php-sha256.phar", PharSignatureType.Sha256)]
  [TestCase("php-md5.phar", PharSignatureType.Md5)]
  [Category("ThirdPartyVector")]
  public void Given_PhpInputs_When_WeWrite_Then_ByteIdenticalToPhp(string vector, PharSignatureType signature) {
    var original = Vector(vector);
    var time = new PharReader(new MemoryStream(original)).Entries[0].Timestamp;
    using var ms = new MemoryStream();
    PharWriter.Write(ms, [
        new PharWriteEntry("a.txt", ATxt, time, 0x1B6, Metadata: "s:4:\"note\";"u8.ToArray()),
        new PharWriteEntry("dir/b.bin", BBin, time, 0x1A4),
        new PharWriteEntry("empty/", [], time, PharWriter.DefaultDirectoryPermissions),
      ],
      stub: "<?php __HALT_COMPILER();"u8.ToArray(), alias: "cwb-" + vector,
      metadata: "a:1:{s:9:\"generator\";s:3:\"php\";}"u8.ToArray(), signature: signature);
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Given_PhpGzipPhar_When_Listed_Then_MethodsAndDirectoriesShow() {
    var list = new PharFormatDescriptor().List(new MemoryStream(Vector("php-gz-sha1.phar")), null);
    Assert.That(list.Select(e => (e.Name, e.Method, e.IsDirectory)), Is.EqualTo(new[] {
      ("a.txt", "gzip", false), ("dir/b.bin", "gzip", false), ("empty", "store", true) }));
  }

  [TestCase("store", PharCompression.None)]
  [TestCase("gzip", PharCompression.Gzip)]
  [TestCase("bzip2", PharCompression.Bzip2)]
  [Category("RoundTrip")]
  public void Given_Files_When_Created_Then_ReadBackIdentical(string method, PharCompression expected) {
    var d = new PharFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", ATxt), ArchiveInputInfo.InMemory("dir/b.bin", BBin),
      new ArchiveInputInfo("empty", "empty", true)], new FormatCreateOptions(method) { FormatSpecific = new() { ["alias"] = "app.phar" } });
    var r = new PharReader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Alias, Is.EqualTo("app.phar"));
    Assert.That(r.SignatureType, Is.EqualTo(PharSignatureType.Sha256));
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "dir/b.bin", "empty/" }));
    Assert.That(r.Entries[1].Compression, Is.EqualTo(expected));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(ATxt));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(BBin));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_EmptySignedArchive() {
    using var ms = new MemoryStream();
    new PharFormatDescriptor().Create(ms, [], new FormatCreateOptions());
    var r = new PharReader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Entries, Is.Empty);
    Assert.That(r.SignatureType, Is.EqualTo(PharSignatureType.Sha256));
  }

  [Test, Category("RoundTrip")]
  public void Given_PhpPhar_When_FileAddedAndRemoved_Then_UntouchedMembersKeepTheirStoredBytes() {
    var original = Vector("php-gz-sha1.phar");
    var before = new PharReader(new MemoryStream(original));
    var storedB = before.ReadStored(before.Entries.Single(e => e.Name == "dir/b.bin"));
    using var ms = new MemoryStream();
    ms.Write(original);
    var d = new PharFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("c.txt", "added"u8.ToArray())]);
    d.Remove(ms, ["a.txt"]);
    var r = new PharReader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Stub, Is.EqualTo(before.Stub));
    Assert.That(r.Alias, Is.EqualTo(before.Alias));
    Assert.That(r.Metadata, Is.EqualTo(before.Metadata));
    Assert.That(r.SignatureType, Is.EqualTo(PharSignatureType.Sha1));
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "dir/b.bin", "empty/", "c.txt" }));
    var b = r.Entries[0];
    Assert.That(r.ReadStored(b), Is.EqualTo(storedB));
    Assert.That((b.Permissions, b.Timestamp), Is.EqualTo((before.Entries[1].Permissions, before.Entries[1].Timestamp)));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_OpenSslSignedPhar_When_Modified_Then_RefusedAndUntouched() {
    var original = Vector("openssl.phar");
    using var ms = new MemoryStream();
    ms.Write(original);
    var d = new PharFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["d"]));
    Assert.Throws<NotSupportedException>(() => d.Add(ms, [ArchiveInputInfo.InMemory("x", [1])]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void Given_TamperedMember_When_Read_Then_SignatureMismatchRefused() {
    var bytes = Vector("php-sha256.phar");
    var at = bytes.AsSpan().IndexOf("hello phar"u8);
    bytes[at] = (byte)'j';
    var ex = Assert.Throws<InvalidDataException>(() => _ = new PharReader(new MemoryStream(bytes)));
    Assert.That(ex!.Message, Does.Contain("signature"));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoHaltCompiler_When_Read_Then_InvalidData() {
    var bytes = Vector("sha1.phar");
    bytes[7] = (byte)'X'; // __HALT_ -> _XHALT_
    Assert.Throws<InvalidDataException>(() => _ = new PharReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ManifestLengthPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("sha1.phar");
    bytes[0x1D + 3] = 0x7F; // manifest length after "<?php __HALT_COMPILER(); ?>\r\n"
    Assert.Throws<InvalidDataException>(() => _ = new PharReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_EntryCountBeyondTheManifest_When_Read_Then_InvalidData() {
    var bytes = Vector("sha1.phar");
    bytes[0x21 + 2] = 0x10; // entry count
    Assert.Throws<InvalidDataException>(() => _ = new PharReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedPhar_When_Read_Then_InvalidData() {
    var bytes = Vector("blog.phar")[..200];
    Assert.Throws<InvalidDataException>(() => _ = new PharReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ZipBasedPhar_When_Read_Then_RefusedAsAnotherLayout() {
    var zip = new byte[64];
    "PK\x03\x04"u8.CopyTo(zip);
    Assert.Throws<InvalidDataException>(() => _ = new PharReader(new MemoryStream(zip)));
  }

  [Test, Category("EdgeCase")]
  public void Given_StubWithoutHaltCompiler_When_Written_Then_Refused()
    => Assert.Throws<ArgumentException>(() => PharWriter.Write(new MemoryStream(), [], stub: "<?php echo 1;"u8.ToArray()));

  [Test, Category("HappyPath")]
  public void Given_PharPath_When_Detected_Then_Phar() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_phar_{Guid.NewGuid():N}.phar");
    File.WriteAllBytes(path, Vector("blog.phar"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Phar"));
    } finally { File.Delete(path); }
  }

  // ── PHP as the oracle for what we write ────────────────────────────────

  private static string? Php() {
    var exe = OperatingSystem.IsWindows() ? "php.exe" : "php";
    return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
      .Where(d => d.Length > 0).Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);
  }

  private static string RunPhp(string php, string arguments) {
    var psi = new ProcessStartInfo(php, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return stdout;
  }

  /// <summary>Loads bz2 from PHP's own ext directory when the build has it but php.ini does not enable it.</summary>
  private static string PhpArguments(string php, bool needBzip2) {
    var args = "-d phar.readonly=0";
    if (!needBzip2 || RunPhp(php, "-m").Split('\n').Any(l => l.Trim() == "bz2")) return args;
    var ext = Path.Combine(Path.GetDirectoryName(php)!, "ext");
    if (!File.Exists(Path.Combine(ext, OperatingSystem.IsWindows() ? "php_bz2.dll" : "bz2.so")))
      Assert.Ignore("PHP has no bz2 extension.");
    return $"{args} -d extension_dir=\"{ext}\" -d extension=bz2";
  }

  [TestCase("store", "sha512")]
  [TestCase("gzip", "sha1")]
  [TestCase("bzip2", "md5")]
  [Category("EndToEnd")]
  public void Given_OurPhar_When_ReadByPhp_Then_PhpListsTheSameMembers(string method, string signature) {
    var php = Php();
    if (php == null) Assert.Ignore("php not on PATH.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_phar_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.phar");
      using (var fs = File.Create(path))
        new PharFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("a.txt", ATxt), ArchiveInputInfo.InMemory("dir/b.bin", BBin),
          new ArchiveInputInfo("empty", "empty", true)],
          new FormatCreateOptions(method) { FormatSpecific = new() { ["alias"] = "ours.phar", ["signature"] = signature } });
      var script = Path.Combine(dir, "list.php");
      File.WriteAllText(script, ListingScript);
      var listing = RunPhp(php, $"{PhpArguments(php, method == "bzip2")} \"{script}\" \"{path}\"");
      AssertMatchesPhp(File.ReadAllBytes(path), Listing(listing, "ours.phar"));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("EndToEnd")]
  public void Given_EditedPhpPhar_When_ReadByPhp_Then_PhpAcceptsTheNewSignature() {
    var php = Php();
    if (php == null) Assert.Ignore("php not on PATH.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_phar_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "edited.phar");
      File.WriteAllBytes(path, Vector("php-sha512-gz.phar"));
      using (var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) {
        var d = new PharFormatDescriptor();
        d.Add(fs, [ArchiveInputInfo.InMemory("new/c.txt", "added"u8.ToArray())]);
        d.Remove(fs, ["a.txt"]);
      }
      var script = Path.Combine(dir, "list.php");
      File.WriteAllText(script, ListingScript);
      var listing = RunPhp(php, $"{PhpArguments(php, false)} \"{script}\" \"{path}\"");
      AssertMatchesPhp(File.ReadAllBytes(path), Listing(listing, "edited.phar"));
      Assert.That(listing, Does.Contain("edited.phar new/c.txt 5 5"));
      Assert.That(listing, Does.Not.Contain("edited.phar a.txt"));
    } finally { Directory.Delete(dir, true); }
  }
}
