using System.Diagnostics;
using System.Text;

namespace Compression.Tests.Iso;

/// <summary>
/// External-tool conformance for the ISO 9660 writer: an image built by
/// <see cref="FileSystem.Iso.IsoWriter"/> must be accepted by the de-facto
/// reference readers <c>isoinfo</c> (cdrtools) and <c>xorriso</c> (libisofs)
/// without any "damaged" / malformed-descriptor complaint, and both must list
/// the files we wrote — including nested-tree files and the long, mixed-case
/// Joliet names.
///
/// <para>ECMA-119 requires every Primary (and Supplementary) Volume Descriptor
/// to fill its mandatory fields: the File Structure Version byte (offset 881 =
/// 1), the a-character / d-character identifier areas (system/volume-set/
/// publisher/data-preparer/application and the copyright/abstract/bibliographic
/// file identifiers) space-padded when unused, the four 17-byte volume
/// date-and-time fields in their "no date" form rather than zero-filled, both
/// the little- and big-endian path-table locations, and a complete root
/// directory record. A descriptor that leaves these zero is rejected by
/// libisofs as a "Wrong or damaged Primary Volume Descriptor"; these tests pin
/// the fix.</para>
///
/// <para>Each test skips cleanly when its tool is absent so the suite stays
/// green on machines without the cdrtools / libisofs binaries.</para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class IsoExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_isoconf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Representative image ─────────────────────────────────────────────────

  /// <summary>
  /// Builds an image exercising the structures most likely to expose descriptor
  /// bugs: a few root files, a two-level nested tree, a directory with ~50
  /// files, and long mixed-case Joliet names.
  /// </summary>
  private string BuildRepresentativeImage() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("README.TXT", "hello iso\n"u8.ToArray());
    w.AddFile("data.bin", new byte[5000]);
    w.AddFile("docs/guide.txt", "guide body\n"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "ref body\n"u8.ToArray());
    w.AddFile("Mixed Case Readme.txt", "long name body\n"u8.ToArray());
    for (var i = 0; i < 50; i++)
      w.AddFile($"big/file{i:D2}.dat", Encoding.ASCII.GetBytes($"content {i}\n"));

    var isoPath = Path.Combine(_tmpDir, "ours.iso");
    File.WriteAllBytes(isoPath, w.Build());
    return isoPath;
  }

  // ── isoinfo (cdrtools) ───────────────────────────────────────────────────

  [Test]
  public void Isoinfo_ReadsDescriptorWithoutError_AndListsFiles() {
    if (!HasCommand("isoinfo")) Assert.Ignore("isoinfo not installed");

    var isoPath = BuildRepresentativeImage();

    // -d dumps the descriptors; any malformed-descriptor diagnostic surfaces here.
    var descriptor = RunTool("isoinfo", $"-d -i \"{isoPath}\"");
    Assert.That(descriptor.ExitCode, Is.Zero,
      $"isoinfo -d failed.\nstdout: {descriptor.StdOut}\nstderr: {descriptor.StdErr}");
    var diag = (descriptor.StdOut + descriptor.StdErr).ToLowerInvariant();
    Assert.That(diag, Does.Not.Contain("damaged"), "isoinfo must not report a damaged descriptor");
    Assert.That(diag, Does.Not.Contain("bad "), "isoinfo must not report a bad descriptor field");
    Assert.That(descriptor.StdOut, Does.Contain("ISO 9660 format"), "isoinfo recognizes the image as ISO 9660");

    // -l lists every directory; the primary tree's uppercased 8.3 names appear.
    var listing = RunTool("isoinfo", $"-l -i \"{isoPath}\"");
    Assert.That(listing.ExitCode, Is.Zero,
      $"isoinfo -l failed.\nstdout: {listing.StdOut}\nstderr: {listing.StdErr}");
    var listed = listing.StdOut.ToUpperInvariant();
    Assert.Multiple(() => {
      Assert.That(listed, Does.Contain("README.TXT"), "root file listed");
      Assert.That(listed, Does.Contain("GUIDE.TXT"), "one-level nested file listed");
      Assert.That(listed, Does.Contain("REFERENCE.TXT"), "two-level nested file listed");
      Assert.That(listed, Does.Contain("/DOCS/API/"), "nested directory path listed");
      Assert.That(listed, Does.Contain("FILE49.DAT"), "last of the ~50 large-directory files listed");
    });
  }

  [Test]
  public void Isoinfo_JolietTree_CarriesLongMixedCaseNames() {
    if (!HasCommand("isoinfo")) Assert.Ignore("isoinfo not installed");

    var isoPath = BuildRepresentativeImage();

    // -J selects the Joliet tree; long, mixed-case names must survive verbatim.
    var listing = RunTool("isoinfo", $"-l -J -i \"{isoPath}\"");
    Assert.That(listing.ExitCode, Is.Zero,
      $"isoinfo -l -J failed.\nstdout: {listing.StdOut}\nstderr: {listing.StdErr}");
    Assert.That(listing.StdOut, Does.Contain("Mixed Case Readme.txt"),
      "Joliet tree carries the long, mixed-case file name");
  }

  // ── xorriso (libisofs) ───────────────────────────────────────────────────

  [Test]
  public void Xorriso_AcceptsDescriptor_AndListsNestedAndJolietNames() {
    if (!HasCommand("xorriso")) Assert.Ignore("xorriso not installed");

    var isoPath = BuildRepresentativeImage();

    // libisofs is strict: a malformed PVD aborts the load with
    // "Wrong or damaged Primary Volume Descriptor". -report_about WARNING raises
    // its sensitivity so even non-fatal descriptor gripes show.
    var toc = RunTool("xorriso", $"-report_about WARNING -abort_on FAILURE -indev \"{isoPath}\" -toc");
    var diag = (toc.StdOut + toc.StdErr).ToLowerInvariant();
    Assert.Multiple(() => {
      Assert.That(diag, Does.Not.Contain("damaged"), "xorriso must not report a damaged descriptor");
      Assert.That(diag, Does.Not.Contain("cannot read iso image tree"), "xorriso must read the image tree");
      Assert.That(diag, Does.Not.Contain("failure"), "xorriso must not hit a FAILURE-level diagnostic");
    });
    Assert.That(toc.ExitCode, Is.Zero,
      $"xorriso -toc failed.\nstdout: {toc.StdOut}\nstderr: {toc.StdErr}");

    // The Joliet tree (preferred by libisofs) exposes the real lowercase /
    // mixed-case names at their nested paths.
    var find = RunTool("xorriso", $"-abort_on FAILURE -indev \"{isoPath}\" -find /");
    Assert.That(find.ExitCode, Is.Zero,
      $"xorriso -find failed.\nstdout: {find.StdOut}\nstderr: {find.StdErr}");
    var found = find.StdOut;
    Assert.Multiple(() => {
      Assert.That(found, Does.Contain("'/README.TXT'"), "root file found");
      Assert.That(found, Does.Contain("'/docs/guide.txt'"), "one-level nested file found at its path");
      Assert.That(found, Does.Contain("'/docs/api/reference.txt'"), "two-level nested file found at its path");
      Assert.That(found, Does.Contain("'/big/file49.dat'"), "large-directory file found");
      Assert.That(found, Does.Contain("'/Mixed Case Readme.txt'"), "long mixed-case Joliet name found");
    });
  }

  // ── External-tool plumbing (mirrors OsIntegrationTests) ──────────────────

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool HasCommand(string name) {
    try {
      var psi = new ProcessStartInfo {
        FileName = "/bin/sh",
        Arguments = $"-c \"command -v {name}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var proc = Process.Start(psi);
      if (proc is null) return false;
      var stdout = proc.StandardOutput.ReadToEnd();
      proc.WaitForExit(10_000);
      return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    } catch {
      return false;
    }
  }

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }
}
