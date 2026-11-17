using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class ArchiveOverlayFilesystemSessionTests {
  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  [Test, Category("Driver")]
  public void WormProbe_IsLogStructuredOnlyWhenExplicitlyRequested() {
    using var archive = BuildArchive();
    var ordinary = MountNamespaceResolver.Probe("Pea", archive);
    var overlay = MountNamespaceResolver.Probe("Pea", archive, preferArchiveOverlay: true);
    Assert.Multiple(() => {
      Assert.That(ordinary.Profile.MutationModel, Is.EqualTo(FilesystemMutationModel.WholeImageRebuild));
      Assert.That(overlay.Profile.MutationModel, Is.EqualTo(FilesystemMutationModel.LogStructured));
      Assert.That(overlay.Profile.CanMountWritable, Is.True);
      Assert.That(overlay.Layers, Does.Contain("namespace:append-only-sidecar"));
      Assert.That(overlay.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Transactions), Is.False);
    });
  }

  [Test, Category("Driver")]
  public void Overlay_PersistsSparseWritesRenamesAndDeletes_WithoutChangingArchive() {
    WithArchive(path => {
      var original = File.ReadAllBytes(path);
      using (var session = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        var root = session.RootNodeId;
        var existing = session.Lookup(root, "existing.txt")!.Value;
        using var originalHandle = session.OpenFile(existing, FileAccess.ReadWrite);
        originalHandle.Write(2, "XY"u8);
        originalHandle.SetLength(12);
        Assert.That(Read(session, existing), Is.EqualTo("beXYre\0\0\0\0\0\0"u8.ToArray()));
        session.Rename(root, "existing.txt", root, "renamed.txt", replace: false);
        Assert.That(session.Lookup(root, "renamed.txt"), Is.EqualTo(existing));

        var folder = session.CreateDirectory(root, "newdir");
        var entry = session.CreateFile(folder, "child.dat");
        using (var writer = session.OpenFile(entry, FileAccess.Write)) {
          writer.Write(6, "tail"u8);
          writer.Flush();
        }
        Assert.That(Read(session, entry), Is.EqualTo(new byte[] {
          0, 0, 0, 0, 0, 0, (byte)'t', (byte)'a', (byte)'i', (byte)'l'
        }));

        var toDelete = session.CreateFile(root, "deleted.dat");
        using (var stale = session.OpenFile(toDelete, FileAccess.ReadWrite)) {
          stale.Write(0, "gone"u8);
          session.DeleteFile(root, "deleted.dat");
          Assert.That(session.Lookup(root, "deleted.dat"), Is.Null);
          Assert.That(ReadHandle(stale), Is.EqualTo("gone"u8.ToArray()),
            "Unlinked nodes must remain accessible via already-open handles.");
        }

        session.Flush();
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
      }

      using (var reopened = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        var root = reopened.RootNodeId;
        Assert.That(reopened.Lookup(root, "existing.txt"), Is.Null);
        Assert.That(reopened.Lookup(root, "deleted.dat"), Is.Null);
        Assert.That(Read(reopened, reopened.Lookup(root, "renamed.txt")!.Value),
          Is.EqualTo("beXYre\0\0\0\0\0\0"u8.ToArray()));
        var folder = reopened.Lookup(root, "newdir")!.Value;
        Assert.That(Read(reopened, reopened.Lookup(folder, "child.dat")!.Value),
          Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 0, 116, 97, 105, 108 }));
        reopened.DeleteFile(folder, "child.dat");
        reopened.RemoveDirectory(root, "newdir");
        reopened.Flush();
      }

      using var final = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path);
      Assert.That(final.Lookup(final.RootNodeId, "newdir"), Is.Null);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
      using var originalReadOnly = new MemoryStream(original, writable: false);
      using var untouched = MountNamespaceResolver.Open(
        "Pea", originalReadOnly, new FilesystemOpenOptions(ReadOnly: true));
      Assert.That(Read(untouched, untouched.Lookup(untouched.RootNodeId, "existing.txt")!.Value),
        Is.EqualTo("before"u8.ToArray()));
    });
  }

  [Test, Category("Driver")]
  public void TruncateThenExtend_DoesNotResurrectSourceBytes() {
    WithArchive(path => {
      using (var session = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        var node = session.Lookup(session.RootNodeId, "existing.txt")!.Value;
        using var handle = session.OpenFile(node, FileAccess.ReadWrite);
        handle.SetLength(2);
        handle.SetLength(6);
        Assert.That(Read(session, node), Is.EqualTo("be\0\0\0\0"u8.ToArray()));
        session.Flush();
      }
      using var reopened = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path);
      var id = reopened.Lookup(reopened.RootNodeId, "existing.txt")!.Value;
      Assert.That(Read(reopened, id), Is.EqualTo("be\0\0\0\0"u8.ToArray()));
    });
  }

  [Test, Category("Driver")]
  public void Recover_OnlyTruncatesInterruptedFinalRecord() {
    WithArchive(path => {
      using (var session = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        var id = session.CreateFile(session.RootNodeId, "survives.txt");
        using var file = session.OpenFile(id, FileAccess.Write);
        file.Write(0, "persisted"u8);
        session.Flush();
      }

      var journal = path + ".cwb-overlay";
      var completeLength = new FileInfo(journal).Length;
      using (var corruptTail = new FileStream(journal, FileMode.Append, FileAccess.Write))
        corruptTail.Write([0x17, 0x00]); // torn record-length prefix
      using (var recovered = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        Assert.That(Read(recovered, recovered.Lookup(recovered.RootNodeId, "survives.txt")!.Value),
          Is.EqualTo("persisted"u8.ToArray()));
      }
      Assert.That(new FileInfo(journal).Length, Is.EqualTo(completeLength));
    });
  }

  [Test, Category("Driver")]
  public void CorruptedCompletedRecord_FailsClosedWithoutDiscardingJournal() {
    WithArchive(path => {
      using (var session = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        session.CreateFile(session.RootNodeId, "new");
        session.Flush();
      }
      var journal = path + ".cwb-overlay";
      var bytes = File.ReadAllBytes(journal);
      bytes[40 + 4 + 32] ^= 0x40; // first byte of the record body
      File.WriteAllBytes(journal, bytes);
      Assert.Throws<InvalidDataException>(() =>
        MountNamespaceResolver.OpenOverlayArchivePath("Pea", path));
      Assert.That(File.ReadAllBytes(journal), Is.EqualTo(bytes));
    });
  }

  [Test, Category("Driver")]
  public void ChangedOriginalArchive_RejectsExistingOverlay() {
    WithArchive(path => {
      using (var session = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path)) {
        session.CreateFile(session.RootNodeId, "modified");
        session.Flush();
      }
      using (var archive = new FileStream(path, FileMode.Append, FileAccess.Write))
        archive.WriteByte(0x55);
      Assert.Throws<InvalidDataException>(() =>
        MountNamespaceResolver.OpenOverlayArchivePath("Pea", path));
    });
  }

  [Test, Category("Driver")]
  public void SameArchive_CannotHaveTwoOverlayWriters() {
    WithArchive(path => {
      using var first = MountNamespaceResolver.OpenOverlayArchivePath("Pea", path);
      Assert.Throws<IOException>(() => MountNamespaceResolver.OpenOverlayArchivePath("Pea", path));
    });
  }

  private static byte[] Read(IFilesystemSession filesystem, FilesystemNodeId id) {
    using var handle = filesystem.OpenFile(id, FileAccess.Read);
    return ReadHandle(handle);
  }

  private static byte[] ReadHandle(IFilesystemFileHandle handle) {
    if (handle.Length > 1024 * 1024)
      throw new AssertionException("Unexpectedly large test file.");
    var result = new byte[(int)handle.Length];
    var read = 0;
    while (read < result.Length) {
      var count = handle.Read(read, result.AsSpan(read));
      if (count == 0) break;
      read += count;
    }
    Assert.That(read, Is.EqualTo(result.Length));
    return result;
  }

  private static MemoryStream BuildArchive() {
    var archive = new MemoryStream();
    var creator = (IArchiveCreatable)(FormatRegistry.GetById("Pea")
      ?? throw new AssertionException("PEA is not registered."));
    creator.Create(archive, [ArchiveInputInfo.InMemory("existing.txt", "before"u8.ToArray())],
      new FormatCreateOptions());
    archive.Position = 0;
    return archive;
  }

  private static void WithArchive(Action<string> test) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-overlay-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "sample.pea");
    try {
      using (var source = BuildArchive())
        File.WriteAllBytes(path, source.ToArray());
      test(path);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
