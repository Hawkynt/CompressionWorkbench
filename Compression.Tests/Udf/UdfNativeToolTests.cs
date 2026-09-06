#pragma warning disable CS1591
using System.Diagnostics;
using System.Text;
using Compression.Tests.Support;
using FileSystem.Udf;

namespace Compression.Tests.Udf;

/// <summary>
/// Checks both directions of the UDF implementation against the software that
/// owns the format on this host: <c>udfinfo</c> and the kernel's <c>udf</c>
/// driver read what we write, and what <c>mkudffs</c> plus that same driver
/// wrote is read back by us.
/// </summary>
/// <remarks>
/// <para>
/// A writer and a reader that only ever meet each other agree on their own
/// mistakes. Every check here therefore has foreign software on one side of it:
/// nothing in this fixture passes because our reader liked our writer.
/// </para>
/// <para>
/// Each test says which tool it needs and stands down when the host does not
/// have it. A tool that is present and rejects the volume is a failure, never
/// a skip.
/// </para>
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class UdfNativeToolTests {

  private const int BlockSize = 2048;

  private string _temp = null!;

  [SetUp]
  public void Setup() {
    this._temp = Path.Combine(Path.GetTempPath(), "cwb_udf_" + Guid.NewGuid().ToString("N")[..12]);
    Directory.CreateDirectory(this._temp);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._temp, recursive: true); } catch { /* best effort */ }
  }

  // ── the volume we write ───────────────────────────────────────────────────

  /// <summary>
  /// Content for a file of <paramref name="size" /> bytes, every byte a
  /// function of its own offset so a misplaced block shows up as wrong bytes
  /// rather than merely a wrong length.
  /// </summary>
  private static byte[] Pattern(int size) {
    var body = new byte[size];
    for (var i = 0; i < size; ++i)
      body[i] = (byte)((i * 2_654_435_761L) >> 13);
    return body;
  }

  /// <summary>
  /// The shapes that break UDF writers: the sizes either side of a logical
  /// block, an empty file, names that need both OSTA compressions, a name at
  /// the identifier's length limit, nesting, and a directory with enough
  /// entries that its File Identifier Descriptors run past one block — which
  /// is where a record has to be allowed to span the boundary.
  /// </summary>
  private static IReadOnlyList<(string Name, byte[] Data)> Corpus() {
    var files = new List<(string, byte[])>();
    foreach (var size in new[] { 0, 1, 2047, 2048, 2049, 4095, 4096, 4097, 65_537 })
      files.Add(($"sizes/size_{size:D8}.bin", Pattern(size)));

    files.Add(("names/" + new string('n', 180) + ".txt", "long identifier\n"u8.ToArray()));
    foreach (var name in new[] { "café.bin", "äöü.txt", "日本語.txt", "русский.dat" })
      files.Add(("names/" + name, Encoding.UTF8.GetBytes(name + "\n")));

    var nested = "";
    for (var depth = 0; depth < 5; ++depth) {
      nested += $"deep{depth}/";
      files.Add((nested + "leaf.bin", Encoding.ASCII.GetBytes($"depth {depth}\n")));
    }

    for (var i = 0; i < 200; ++i)
      files.Add(($"many/entry_{i:D4}.txt", Encoding.ASCII.GetBytes($"entry number {i}\n")));

    return files;
  }

  private string WriteOurVolume(string fileName = "ours.img") {
    var writer = new UdfWriter { VolumeIdentifier = "CWBUDF" };
    foreach (var (name, data) in Corpus())
      writer.AddFile(name, data);

    var path = Path.Combine(this._temp, fileName);
    using (var output = File.Create(path))
      writer.WriteTo(output);
    return path;
  }

  // ── write direction ───────────────────────────────────────────────────────

  /// <summary>
  /// <c>udfinfo</c> is the format's own tool, and it reports every structure a
  /// UDF volume is supposed to carry that is missing. It exits zero either way,
  /// so the volume is only accepted when it also had nothing to say: before the
  /// reserve descriptor sequence, the second anchor, the unallocated space and
  /// implementation use descriptors and the integrity descriptor were written,
  /// it printed seven warnings and called the logical volume inconsistent.
  /// </summary>
  [Test]
  public void UdfinfoAcceptsOurVolumeWithoutWarnings() {
    var udfinfo = Which("udfinfo");
    if (udfinfo == null)
      Assert.Ignore("udfinfo is not installed (udftools).");

    var image = this.WriteOurVolume();
    var (stdout, stderr, exit) = Run(udfinfo!, Quote(image));

    Assert.Multiple(() => {
      Assert.That(exit, Is.Zero, $"udfinfo exit {exit}\n{stdout}\n{stderr}");
      Assert.That(stderr.Trim(), Is.Empty, $"udfinfo complained about our volume:\n{stderr}");
    });

    var fields = ParseFields(stdout);
    Assert.Multiple(() => {
      Assert.That(fields.GetValueOrDefault("label"), Is.EqualTo("CWBUDF"),
        "the volume identifier is a dstring: a compression byte, the characters, and the used length in the field's last byte");
      Assert.That(fields.GetValueOrDefault("lvid"), Is.EqualTo("CWBUDF"));
      Assert.That(fields.GetValueOrDefault("fsid"), Is.EqualTo("CWBUDF"));
      Assert.That(fields.GetValueOrDefault("integrity"), Is.EqualTo("closed"),
        "without a logical volume integrity descriptor the volume reads as inconsistent");
      Assert.That(fields.GetValueOrDefault("udfrev"), Is.EqualTo("2.01"));
      Assert.That(fields.GetValueOrDefault("blocksize"), Is.EqualTo("2048"));
      Assert.That(fields.GetValueOrDefault("numfiles"), Is.EqualTo(Corpus().Count.ToString()));
    });
  }

  /// <summary>
  /// The kernel's udf driver reads a directory as one uninterrupted run of File
  /// Identifier Descriptors and lets a record span a block boundary. Padding
  /// the boundary instead made it stop at the first pad byte — "entry at pos
  /// 2020 with incorrect tag 0" — and lose every entry past it, which is most
  /// of a directory of any size.
  /// </summary>
  [Test]
  public void TheKernelDriverReadsEveryFileBackFromOurVolume() {
    var image = this.WriteOurVolume();
    var expected = Corpus().Select(static entry => entry.Data).ToList();

    var result = ThirdPartyFsCheck.ReadBack("Udf", image, expected);
    if (!result.Ran)
      Assert.Ignore(result.Detail);

    Assert.That(result.Ok, Is.True, $"{result.Tool}: {result.Detail}");
  }

  // ── read direction ────────────────────────────────────────────────────────

  /// <summary>
  /// A volume neither our writer nor our reader had a hand in: <c>mkudffs</c>
  /// formats it, the kernel fills it, and only then does our reader see it.
  /// </summary>
  /// <remarks>
  /// Run for each block size mkudffs will format, because the anchor sits at
  /// logical block 256 and that address is counted in blocks: assuming 2048
  /// looked for it in the wrong place on every other volume and the reader
  /// threw before it read anything at all. At 512 bytes a block, a directory of
  /// two hundred entries also outgrows the descriptors its File Entry has room
  /// for, so the rest of them live in a continuation extent the reader has to
  /// follow.
  /// </remarks>
  [TestCase(512)]
  [TestCase(1024)]
  [TestCase(2048)]
  [TestCase(4096)]
  public void OurReaderReadsAVolumeTheNativeToolsBuilt(int blockSize) {
    var mkudffs = Which("mkudffs");
    if (mkudffs == null)
      Assert.Ignore("mkudffs is not installed (udftools).");
    if (!CanMount)
      Assert.Ignore("filling a volume needs the kernel's udf driver and passwordless sudo.");

    var image = Path.Combine(this._temp, $"native_{blockSize}.img");
    using (var file = File.Create(image))
      file.SetLength(32L * 1024 * 1024);

    var (mkStdout, mkStderr, mkExit) = Run(mkudffs!,
      $"--media-type=hd --blocksize={blockSize} --udfrev=2.01 --label=NATIVE {Quote(image)}");
    Assert.That(mkExit, Is.Zero, $"mkudffs exit {mkExit}\n{mkStdout}\n{mkStderr}");

    var written = FillThroughTheKernel(image);
    if (written == null)
      Assert.Ignore("the kernel refused to mount a volume mkudffs had just made writable.");

    using var stream = File.OpenRead(image);
    using var reader = new UdfReader(stream, leaveOpen: true);

    var got = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      using var buffer = new MemoryStream();
      reader.ExtractTo(entry, buffer);
      got[entry.Name] = buffer.ToArray();
    }

    Assert.Multiple(() => {
      foreach (var (name, data) in written!) {
        Assert.That(got.ContainsKey(name), Is.True, $"our reader lost {name}");
        if (got.TryGetValue(name, out var actual))
          Assert.That(actual, Is.EqualTo(data), $"{name} came back with different bytes");
      }

      Assert.That(got, Has.Count.EqualTo(written.Count),
        "our reader invented entries the kernel did not put there");
    });
  }

  /// <summary>
  /// Mounts <paramref name="image" /> writable, writes the corpus into it with
  /// the kernel's own driver, and returns what was written keyed by the path
  /// our reader will report. Null when the volume would not mount.
  /// </summary>
  private static IReadOnlyDictionary<string, byte[]>? FillThroughTheKernel(string image) {
    var mountPoint = Path.Combine(Path.GetTempPath(), "cwb_udfrw_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(mountPoint);
    try {
      var uid = Run("id", "-u").StdOut.Trim();
      var gid = Run("id", "-g").StdOut.Trim();
      var (_, stderr, exit) = Run("sudo",
        $"-n mount -t udf -o loop,noatime,uid={uid},gid={gid} {Quote(image)} {Quote(mountPoint)}");
      if (exit != 0) {
        TestContext.Out.WriteLine($"mount refused the mkudffs volume: {stderr}");
        return null;
      }

      try {
        var written = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, data) in Corpus()) {
          var target = Path.Combine(mountPoint, name);
          Directory.CreateDirectory(Path.GetDirectoryName(target)!);
          File.WriteAllBytes(target, data);
          written[name] = data;
        }

        return written;
      } finally {
        Run("sudo", $"-n umount {Quote(mountPoint)}");
      }
    } finally {
      try { Directory.Delete(mountPoint, recursive: true); } catch { /* the mount point may be gone */ }
    }
  }

  // ── process plumbing ──────────────────────────────────────────────────────

  private static readonly bool CanMount = OperatingSystem.IsLinux() && Run("sudo", "-n true").Exit == 0;

  private static Dictionary<string, string> ParseFields(string stdout) {
    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in stdout.Split('\n')) {
      var split = line.IndexOf('=');
      if (split > 0)
        fields[line[..split].Trim()] = line[(split + 1)..].Trim();
    }

    return fields;
  }

  private static string Quote(string path) => "\"" + path + "\"";

  private static string? Which(string tool) {
    var (stdout, _, exit) = Run("which", tool);
    var path = stdout.Trim();
    return exit == 0 && path.Length > 0 ? path : null;
  }

  private static (string StdOut, string StdErr, int Exit) Run(string file, string arguments) {
    try {
      var start = new ProcessStartInfo(file, arguments) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var process = Process.Start(start);
      if (process == null) return ("", "could not start " + file, -1);
      var stdout = process.StandardOutput.ReadToEnd();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit(120_000);
      return (stdout, stderr, process.ExitCode);
    } catch (Exception ex) {
      return ("", ex.Message, -1);
    }
  }
}
