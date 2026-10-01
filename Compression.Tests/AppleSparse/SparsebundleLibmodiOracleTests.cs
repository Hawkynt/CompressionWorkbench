using System.Diagnostics;
using System.Security.Cryptography;
using FileFormat.AppleSparse;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.AppleSparse;

/// <summary>
/// Our sparsebundle reader judged by an independent one: libyal's libmodi (LGPL), through its
/// <c>pymodi</c> Python binding (<c>pip install libmodi-python</c>). For each bundle libmodi must
/// report the same media size and read the same media bytes as <see cref="SparsebundleReader"/>.
/// The real hdiutil bundles keep the oracle itself honest; the derived ones (a missing band, a
/// short band) pin the sparse cases. Ignored where no Python with pymodi is on the PATH.
/// </summary>
/// <remarks>
/// The script hands libmodi the band files as file objects. Measured with libmodi-python
/// 20260902 on Windows, <c>open_band_data_files()</c> opened no band and every read returned
/// zeros, while the file-object path reads the published media hashes. pymodi refuses a missing
/// entry in that list, so an absent band is handed over as an empty file, which libmodi, like
/// hdiutil, reads as zeros.
/// </remarks>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class SparsebundleLibmodiOracleTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_sbmodi_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        if (Run(candidate, ["-c", "import pymodi"]).ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  /// <summary>Prints "media-size sha256" of the media libmodi reads from the bundle.</summary>
  private const string ReadScript = """
    import hashlib, io, os, re, sys, pymodi
    root = os.path.abspath(sys.argv[1])
    handle = pymodi.handle()
    handle.open(os.path.join(root, 'Info.plist'))
    size = handle.get_media_size()
    plist = open(os.path.join(root, 'Info.plist'), 'rb').read().decode('utf-8')
    band = int(re.search(r'<key>band-size</key>\s*<integer>(\d+)</integer>', plist).group(1))
    bands = []
    for index in range((size + band - 1) // band):
        path = os.path.join(root, 'bands', format(index, 'x'))
        bands.append(open(path, 'rb') if os.path.exists(path) else io.BytesIO(b''))
    handle.open_band_data_files_as_file_objects(bands)
    digest = hashlib.sha256()
    offset = 0
    while offset < size:
        chunk = handle.read_buffer_at_offset(min(1 << 20, size - offset), offset)
        if not chunk:
            break
        digest.update(chunk)
        offset += len(chunk)
    print(size, offset, digest.hexdigest())
    """;

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }

  private static (long Size, long Read, string Sha256) Libmodi(string bundle) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with pymodi on the PATH (`pip install libmodi-python`).");
    var (code, stdout, stderr) = Run(python!, ["-c", ReadScript, bundle]);
    Assert.That(code, Is.EqualTo(0), $"libmodi could not read {Path.GetFileName(bundle)}:\n{stderr}");
    var fields = stdout.Trim().Split(' ');
    return (long.Parse(fields[0]), long.Parse(fields[1]), fields[2]);
  }

  private static void AssertSameMedia(string bundle) {
    var (size, read, sha256) = Libmodi(bundle);
    var reader = new SparsebundleReader(bundle);
    Assert.Multiple(() => {
      Assert.That(size, Is.EqualTo(reader.VirtualSize), "media size");
      Assert.That(read, Is.EqualTo(size), "libmodi read the whole medium");
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractDisk())), Is.EqualTo(sha256), "media bytes");
    });
  }

  [TestCase(SparsebundleVectors.HfsPlus, TestName = "GivenTheHdiutilHfsPlusBundle_WhenLibmodiReadsIt_ThenOurReaderReadsTheSameMedia")]
  [TestCase(SparsebundleVectors.Ewfprobe, TestName = "GivenTheHdiutilUdsbBundle_WhenLibmodiReadsIt_ThenOurReaderReadsTheSameMedia")]
  [Category("HappyPath")]
  public void RealBundles(string bundle) => AssertSameMedia(SparsebundleVectors.Materialize(bundle, this._tmp));

  [Test, Category("BoundaryCase")]
  public void GivenAMissingMiddleBand_WhenLibmodiReadsIt_ThenOurReaderReadsTheSameMedia() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    File.Delete(Path.Combine(root, "bands", "1"));
    AssertSameMedia(root);
  }

  [Test, Category("BoundaryCase")]
  public void GivenABandFileShorterThanTheBandSize_WhenLibmodiReadsIt_ThenOurReaderReadsTheSameMedia() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    var band = Path.Combine(root, "bands", "0");
    // Sector-aligned, as hdiutil leaves a band it has not filled: libmodi 20260902 fails on the
    // final partial sector of a band whose length is not a multiple of 512 (measured with 12345).
    File.WriteAllBytes(band, File.ReadAllBytes(band)[..(24 * 512)]);
    AssertSameMedia(root);
  }
}
