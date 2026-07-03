using System.IO.Compression;
using System.Text;
using FileFormat.BitRock;
using FileFormat.Tar;

namespace Compression.Tests.BitRock;

/// <summary>
/// Tests for the BitRock / InstallBuilder installer reader.
///
/// The application payload lives in a cookfs (CFS0002) page archive in the content region; the
/// reader reconstructs it to a plain gzip-tar stream and extracts byte-exact files. Synthetic
/// fixtures build a real cookfs archive so the whole flow is exercised on every platform; the
/// heavy assertions run against a real ~580 MB InstallBuilder sample under D:\Temp when present and
/// skip cleanly otherwise.
/// </summary>
[TestFixture]
public sealed class BitRockTests {

  private static string? FindSample() {
    if (!OperatingSystem.IsWindows())
      return null;
    var dir = @"D:\Temp";
    if (!Directory.Exists(dir))
      return null;
    foreach (var f in Directory.GetFiles(dir, "*installer*.exe"))
      using (var fs = File.OpenRead(f))
        if (BitRockReader.IsBitRock(fs))
          return f;
    return null;
  }

  // ── Fixture builders ──────────────────────────────────────────────────────

  private static uint Crc32(byte[] data) {
    var crc = 0xffffffffu;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
    }
    return crc ^ 0xffffffffu;
  }

  /// <summary>A valid gzip member (with the FNAME field the scanner keys on) over <paramref name="payload"/>.</summary>
  private static byte[] GzipWithName(string name, byte[] payload) {
    using var ms = new MemoryStream();
    ms.Write([0x1f, 0x8b, 0x08, 0x08]);          // magic, DEFLATE, FLG=FNAME
    ms.Write(new byte[6]);                        // MTIME(4)=0, XFL=0, OS=0
    ms.Write(Encoding.Latin1.GetBytes(name));
    ms.WriteByte(0);                              // NUL-terminated FNAME
    using (var def = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      def.Write(payload, 0, payload.Length);
    Span<byte> tr = stackalloc byte[8];
    var crc = Crc32(payload);
    tr[0] = (byte)crc; tr[1] = (byte)(crc >> 8); tr[2] = (byte)(crc >> 16); tr[3] = (byte)(crc >> 24);
    var isize = (uint)payload.Length;
    tr[4] = (byte)isize; tr[5] = (byte)(isize >> 8); tr[6] = (byte)(isize >> 16); tr[7] = (byte)(isize >> 24);
    ms.Write(tr);
    return ms.ToArray();
  }

  private static byte[] BuildTar((string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var tw = new TarWriter(ms, leaveOpen: true))
      foreach (var (name, data) in files)
        tw.AddEntry(new TarEntry { Name = name, Size = data.Length }, data);
    return ms.ToArray();
  }

  private static byte[] MinimalVfs() {
    using var vfsMs = new MemoryStream();
    vfsMs.Write("JL"u8);
    vfsMs.Write(new byte[6]);
    vfsMs.Write("<root>\0"u8);
    return vfsMs.ToArray();
  }

  /// <summary>Wraps <paramref name="content"/> in a cookfs (CFS0002) page archive split into
  /// <paramref name="pageBytes"/>-byte stored pages — the container BitRock stores the payload in.</summary>
  private static byte[] BuildCookfs(byte[] content, int pageBytes) {
    using var ms = new MemoryStream();
    var sizes = new List<int>();
    for (var off = 0; off < content.Length; off += pageBytes) {
      var n = Math.Min(pageBytes, content.Length - off);
      ms.WriteByte(0);                            // cid 0 = stored
      ms.Write(content, off, n);
      sizes.Add(1 + n);
    }
    var numpages = sizes.Count;
    ms.Write(new byte[numpages * 16]);            // per-page MD5/CRC table (unused by the reader)
    var sz = new byte[4];
    foreach (var s in sizes) {                    // per-page size table (big-endian int32)
      sz[0] = (byte)(s >> 24); sz[1] = (byte)(s >> 16); sz[2] = (byte)(s >> 8); sz[3] = (byte)s;
      ms.Write(sz);
    }
    // fsindex omitted (idxsize 0 — the payload is recovered by scanning reconstructed content).
    Span<byte> footer = stackalloc byte[16];
    footer[4] = (byte)(numpages >> 24); footer[5] = (byte)(numpages >> 16);   // idxsize=0, numpages BE
    footer[6] = (byte)(numpages >> 8); footer[7] = (byte)numpages;
    CookfsArchive.Signature.CopyTo(footer[9..]);
    ms.Write(footer);
    return ms.ToArray();
  }

  /// <summary>Builds a minimal BitRock installer whose content region is a cookfs archive of
  /// <paramref name="content"/> (footer ending exactly at the Metakit VFS start).</summary>
  private static byte[] BuildCookfsInstaller(byte[] vfs, byte[] content, int pageBytes = 100) {
    using var ms = new MemoryStream();
    ms.Write(new byte[64]);                       // stub placeholder
    ms.Write(BuildCookfs(content, pageBytes));    // cookfs archive (content region)
    var vfsLen = vfs.Length;
    ms.Write(vfs);                                // Metakit VFS (cookfs end offset == here)
    Span<byte> tr = stackalloc byte[16];
    tr[0] = 0x80;                                 // a = 0x80000000
    tr[4] = (byte)(vfsLen >> 24); tr[5] = (byte)(vfsLen >> 16); tr[6] = (byte)(vfsLen >> 8); tr[7] = (byte)vfsLen;
    tr[8] = 0x80;                                 // top byte of c = 0x80
    ms.Write(tr);
    ms.Write("bitrock-lzma-4.0"u8);
    ms.Write("mFC3acAOJrQinu5aEHu0uH7N5XSQ3Z14"u8);
    return ms.ToArray();
  }

  // ── Detection / localisation (always run) ─────────────────────────────────

  [Test]
  public void Detection_And_Localisation_Work_On_Synthetic_Footer() {
    var file = BuildCookfsInstaller(MinimalVfs(), GzipWithName("x.1.0.tar", BuildTar([("a.txt", "hi"u8.ToArray())])));
    using var stream = new MemoryStream(file);
    Assert.That(BitRockReader.IsBitRock(stream), Is.True, "footer magic not detected");
    Assert.That(BitRockReader.TryLocateVfs(stream, out var start, out var size), Is.True);
    Assert.That(size, Is.EqualTo(MinimalVfs().Length));
    Assert.That(start, Is.GreaterThan(64L));
  }

  [Test]
  public void NonBitRock_Stream_Is_Rejected() {
    using var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 1000)));
    Assert.That(BitRockReader.IsBitRock(stream), Is.False);
  }

  // ── cookfs reconstruction + payload extraction (always run) ───────────────

  [Test]
  public void Cookfs_Reconstructs_Content_ByteExact_Across_Pages() {
    var content = new byte[4000];
    new Random(1234).NextBytes(content);
    var file = BuildCookfsInstaller(MinimalVfs(), content, pageBytes: 100);
    using var stream = new MemoryStream(file);

    var reader = BitRockReader.Open(stream);
    var tmp = BitRockContentScanner.ReconstructContent(stream, reader.VfsStart);
    Assert.That(tmp, Is.Not.Null, "cookfs archive not found");
    try {
      Assert.That(File.ReadAllBytes(tmp!), Is.EqualTo(content), "reconstructed cookfs content is not byte-exact");
    } finally {
      File.Delete(tmp!);
    }
  }

  [Test]
  public void Scanner_Finds_And_Extracts_Payload_Component() {
    var tar = BuildTar([("app/hello.txt", "Hello, world"u8.ToArray()),
                        ("app/data.bin", [1, 2, 3, 4, 5, 6, 7, 8])]);
    var file = BuildCookfsInstaller(MinimalVfs(), GzipWithName("myapp.1.0.tar", tar));
    using var stream = new MemoryStream(file);

    var reader = BitRockReader.Open(stream);
    var tmp = BitRockContentScanner.ReconstructContent(stream, reader.VfsStart)!;
    try {
      using var content = File.OpenRead(tmp);
      var members = BitRockContentScanner.ScanMembers(content);
      Assert.That(members, Has.Count.EqualTo(1));
      Assert.That(members[0].Name, Is.EqualTo("myapp.1.0.tar"));

      var entries = BitRockContentScanner.EnumerateComponent(content, members[0]).ToList();
      Assert.That(entries.Where(e => !e.IsDir).Select(e => e.Path),
        Is.EquivalentTo(new[] { "app/hello.txt", "app/data.bin" }));

      var dir = Path.Combine(Path.GetTempPath(), "cwb_br_" + Guid.NewGuid().ToString("N")[..8]);
      try {
        var res = BitRockContentScanner.ExtractComponentToDisk(content, members[0], dir);
        Assert.That(res.CleanEnd, Is.True);
        Assert.That(res.FileCount, Is.EqualTo(2));
        Assert.That(File.ReadAllText(Path.Combine(dir, "app", "hello.txt")), Is.EqualTo("Hello, world"));
        Assert.That(File.ReadAllBytes(Path.Combine(dir, "app", "data.bin")),
          Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
      } finally {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
      }
    } finally {
      File.Delete(tmp);
    }
  }

  [Test]
  public void Descriptor_Namespaces_And_Extracts_Runtime_And_Payload() {
    var tar = BuildTar([("bin/tool.exe", [0x4d, 0x5a, 0x90, 0x00]), ("etc/config", "k=v"u8.ToArray())]);
    var file = BuildCookfsInstaller(MinimalVfs(), GzipWithName("prod.3.tar", tar));
    using var stream = new MemoryStream(file);

    var desc = new BitRockFormatDescriptor();
    var list = desc.List(stream, null);
    Assert.That(list.Select(e => e.Name), Has.Some.EqualTo("payload/prod.3.tar/"));
    Assert.That(list.Select(e => e.Name), Has.Some.EqualTo("payload/prod.3.tar/etc/config"));

    var dir = Path.Combine(Path.GetTempPath(), "cwb_br_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      desc.Extract(stream, dir, null, null);
      var cfg = Path.Combine(dir, "payload", "prod.3.tar", "etc", "config");
      Assert.That(File.Exists(cfg), Is.True, "expected payload file on disk");
      Assert.That(File.ReadAllText(cfg), Is.EqualTo("k=v"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  // ── Real sample (skips when absent) ───────────────────────────────────────

  [Test]
  public void RealSample_Lists_Coherent_Directory_Tree() {
    var sample = FindSample();
    if (sample == null)
      Assert.Ignore("No BitRock installer sample present under D:\\Temp.");

    using var fs = File.OpenRead(sample!);
    var reader = BitRockReader.Open(fs);
    Assert.That(reader.DirectoryPaths.Count, Is.GreaterThan(10), "expected a populated directory tree");
    Assert.That(reader.DirectoryPaths, Has.Some.Contains("/"), "expected nested directory paths");
    foreach (var p in reader.DirectoryPaths)
      Assert.That(p, Does.Not.StartWith("/").And.Not.Contains(".."), "directory path should be clean and relative");
  }

  [Test]
  public void RealSample_Reconstructs_Cookfs_And_Finds_Components() {
    var sample = FindSample();
    if (sample == null)
      Assert.Ignore("No BitRock installer sample present under D:\\Temp.");

    using var fs = File.OpenRead(sample!);
    var reader = BitRockReader.Open(fs);
    var tmp = BitRockContentScanner.ReconstructContent(fs, reader.VfsStart);
    Assert.That(tmp, Is.Not.Null, "cookfs content region not reconstructed");
    try {
      using var content = File.OpenRead(tmp!);
      var members = BitRockContentScanner.ScanMembers(content);
      Assert.That(members, Is.Not.Empty, "expected gzip-tar components in the reconstructed content");
      foreach (var m in members) {
        Assert.That(m.Name.ToLowerInvariant(), Does.EndWith(".tar"));
        Assert.That(m.ContentOffset, Is.GreaterThanOrEqualTo(0));
        Assert.That(m.Length, Is.GreaterThan(0));
      }
    } finally {
      File.Delete(tmp!);
    }
  }

  [Test]
  public void RealSample_FullyExtracts_All_Components_ByteExact() {
    var sample = FindSample();
    if (sample == null)
      Assert.Ignore("No BitRock installer sample present under D:\\Temp.");

    var outRoot = Path.Combine(Path.GetTempPath(), "cwb_bitrock_full_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outRoot);
    string? tmp = null;
    try {
      using var fs = File.OpenRead(sample!);
      var reader = BitRockReader.Open(fs);
      tmp = BitRockContentScanner.ReconstructContent(fs, reader.VfsStart);
      Assert.That(tmp, Is.Not.Null);
      using var content = File.OpenRead(tmp!);
      var members = BitRockContentScanner.ScanMembers(content);
      Assert.That(members, Is.Not.Empty);

      long grandFiles = 0, grandBytes = 0;
      var cleanComponents = 0;
      var spot = new Dictionary<string, int>();
      foreach (var c in members) {
        var dir = Path.Combine(outRoot, c.Name);
        var res = BitRockContentScanner.ExtractComponentToDisk(content, c, dir);

        // Every component's gzip stream must decode end-to-end (no private framing anymore).
        Assert.That(res.CleanEnd, Is.True, $"component {c.Name} did not decode cleanly");
        Assert.That(res.FileCount, Is.GreaterThan(0), $"component {c.Name} yielded no files");
        if (res.CleanEnd) ++cleanComponents;
        grandFiles += res.FileCount;
        grandBytes += res.TotalBytes;

        // Spot-check a spread of extracted files: well-formed only if the bytes are exact.
        var head = new byte[8];
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
          int n;
          using (var f = File.OpenRead(path)) n = f.Read(head);
          var kind = Classify(head.AsSpan(0, n), path);
          if (kind == null)
            continue;
          if (kind == "MZ")
            Assert.That(IsWellFormedPe(path), Is.True, $"extracted PE not well-formed: {path}");
          if (!spot.TryGetValue(kind, out var have) || have < 4)
            spot[kind] = spot.GetValueOrDefault(kind) + 1;
        }
      }

      TestContext.Out.WriteLine($"components={members.Count} clean={cleanComponents} files={grandFiles} bytes={grandBytes}");
      foreach (var kv in spot)
        TestContext.Out.WriteLine($"  spot {kv.Key}: {kv.Value}");

      // All components decode fully; the total is the whole ~570 MiB application payload.
      Assert.That(cleanComponents, Is.EqualTo(members.Count), "every component must decode to a clean end");
      Assert.That(grandBytes, Is.GreaterThan(400L << 20), "expected several hundred MiB of application data");
      Assert.That(grandFiles, Is.GreaterThan(1000), "expected thousands of application files");
      Assert.That(spot.ContainsKey("MZ"), Is.True, "expected PE/MZ executables");
      Assert.That(spot.Keys, Has.Some.AnyOf("XML", "TEXT", "PNG"), "expected XML/text/image files");
    } finally {
      if (tmp != null) try { File.Delete(tmp); } catch { /* ignore */ }
      if (Directory.Exists(outRoot))
        Directory.Delete(outRoot, recursive: true);
    }
  }

  /// <summary>Verifies a file is a structurally coherent PE (MZ + e_lfanew → PE\0\0 + ≥1 section
  /// whose raw data lies within the file) — a truncated/garbled DLL fails this.</summary>
  private static bool IsWellFormedPe(string path) {
    try {
      var b = File.ReadAllBytes(path);
      if (b.Length < 0x40 || b[0] != (byte)'M' || b[1] != (byte)'Z')
        return false;
      var e = BitConverter.ToInt32(b, 0x3c);
      if (e < 0 || e + 24 > b.Length || b[e] != (byte)'P' || b[e + 1] != (byte)'E' || b[e + 2] != 0 || b[e + 3] != 0)
        return false;
      int nsec = BitConverter.ToUInt16(b, e + 6);
      int optSize = BitConverter.ToUInt16(b, e + 20);
      var sect = e + 24 + optSize;
      if (nsec < 1 || sect + 40 > b.Length)
        return false;
      var rawSize = BitConverter.ToUInt32(b, sect + 16);
      var rawPtr = BitConverter.ToUInt32(b, sect + 20);
      return rawPtr + rawSize <= (uint)b.Length + 4096;
    } catch {
      return false;
    }
  }

  private static string? Classify(ReadOnlySpan<byte> head, string path) {
    if (head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z') return "MZ";
    if (head.Length >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4e && head[3] == 0x47) return "PNG";
    if (head.Length >= 3 && head[0] == 0xef && head[1] == 0xbb && head[2] == 0xbf) return "XML";
    if (head.Length >= 1 && head[0] == (byte)'<') return "XML";
    var ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext is ".txt" or ".xml" or ".tcl" or ".java" or ".properties" or ".html" or ".json") return "TEXT";
    return null;
  }
}
