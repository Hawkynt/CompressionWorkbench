using System.Runtime.InteropServices;
using Compression.Lib;

namespace Compression.Tests.Lib;

[TestFixture]
public class ArchiveInputTests {

  [Test, Category("HappyPath")]
  public void Resolve_RecursesNestedDirectories() {
    var root = Path.Combine(Path.GetTempPath(), "cwb-ai-" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
      File.WriteAllText(Path.Combine(root, "top.txt"), "a");
      File.WriteAllText(Path.Combine(root, "sub", "mid.txt"), "b");
      File.WriteAllText(Path.Combine(root, "sub", "deep", "low.txt"), "c");

      var entries = ArchiveInput.Resolve([root]);
      var files = entries.Where(e => !e.IsDirectory)
                         .Select(e => Path.GetFileName(e.FullPath)).ToHashSet();
      Assert.That(files, Does.Contain("top.txt"));
      Assert.That(files, Does.Contain("mid.txt"));
      Assert.That(files, Does.Contain("low.txt"));
    } finally {
      TryDeleteTree(root);
    }
  }

  [Test, Category("ErrorHandling")]
  public void Resolve_SkipsInaccessibleSubdirectory_DoesNotThrow() {
    // Regression: Resolve used Directory.GetFiles(..., SearchOption.AllDirectories)
    // which throws UnauthorizedAccessException on the first locked-down subtree,
    // aborting the whole "Create archive" operation. It must now skip such dirs.
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
      Assert.Ignore("POSIX chmod-based permission test; not meaningful on Windows.");
      return;
    }

    var root = Path.Combine(Path.GetTempPath(), "cwb-ai-" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(root);
      File.WriteAllText(Path.Combine(root, "readable.txt"), "ok");
      var locked = Path.Combine(root, "locked");
      Directory.CreateDirectory(locked);
      File.WriteAllText(Path.Combine(locked, "secret.txt"), "nope");

      // Remove all permissions on the subdirectory.
      File.SetUnixFileMode(locked, UnixFileMode.None);

      // If the restriction didn't actually take effect (e.g. running as root or
      // an unusual filesystem), the negative assertion below would be invalid.
      var actuallyRestricted = false;
      try { _ = Directory.GetFiles(locked); }
      catch (UnauthorizedAccessException) { actuallyRestricted = true; }

      List<ArchiveInput> entries = null!;
      Assert.DoesNotThrow(() => entries = ArchiveInput.Resolve([root]),
        "Resolve must not throw when a subdirectory is inaccessible");

      var files = entries.Where(e => !e.IsDirectory)
                         .Select(e => Path.GetFileName(e.FullPath)).ToHashSet();
      Assert.That(files, Does.Contain("readable.txt"), "accessible files must still be collected");
      if (actuallyRestricted)
        Assert.That(files, Does.Not.Contain("secret.txt"), "files under a locked dir are skipped");
    } finally {
      // Restore permissions so cleanup can delete the tree.
      var locked = Path.Combine(root, "locked");
      try {
        if (Directory.Exists(locked))
          File.SetUnixFileMode(locked,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      } catch { /* best effort */ }
      TryDeleteTree(root);
    }
  }

  private static void TryDeleteTree(string path) {
    try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    catch { /* leftover temp dir — non-fatal */ }
  }
}
