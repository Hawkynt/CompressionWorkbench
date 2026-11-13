using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class ArchiveFileBackedMountTests {
  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  [Test]
  public void Zip_StagesSparseFileBeyondTwoGigabytesWithoutAllocatingAnArray() {
    using var archive = CreateZip([ArchiveInputInfo.InMemory("large.bin", "x"u8.ToArray())]);
    var original = archive.ToArray();
    using (var session = MountNamespaceResolver.Open("Zip", archive,
      new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var file = session.Lookup(session.RootNodeId, "large.bin");
      Assert.That(file, Is.Not.Null);
      using var handle = session.OpenFile(file!.Value, FileAccess.ReadWrite);
      const long huge = (long)int.MaxValue + 8192;
      handle.SetLength(huge);
      Assert.That(handle.Length, Is.EqualTo(huge));

      var zeros = new byte[32];
      Assert.That(handle.Read(huge - zeros.Length, zeros), Is.EqualTo(zeros.Length));
      Assert.That(zeros, Is.All.Zero);

      // Avoid writing a multi-GB fixture during ordinary unit testing.
      handle.SetLength(2);
      handle.Write(1, "y"u8);
      session.Flush();
    }
    Assert.That(archive.ToArray(), Is.Not.EqualTo(original));
    archive.Position = 0;
    var ops = FormatRegistry.GetArchiveOps("Zip")!;
    Assert.That(ops.ExtractEntryToMemory(archive, "large.bin", null),
      Is.EqualTo("xy"u8.ToArray()));
  }

  [Test, Category("LargeData")]
  public void Zip_PublishesEntryLargerThanFormerStagingLimit() {
    using var archive = CreateZip([ArchiveInputInfo.InMemory("existing.txt", "still here"u8.ToArray())]);
    const long large = 260L * 1024 * 1024 + 1;
    using (var session = MountNamespaceResolver.Open("Zip", archive,
      new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var file = session.CreateFile(session.RootNodeId, "large.bin");
      using (var handle = session.OpenFile(file, FileAccess.ReadWrite)) {
        handle.SetLength(large);
        handle.Write(large - 1, [0x5A]);
      }
      session.Flush();
    }

    archive.Position = 0;
    using var reopened = MountNamespaceResolver.Open("Zip", archive,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var fileId = reopened.Lookup(reopened.RootNodeId, "large.bin");
    Assert.That(fileId, Is.Not.Null);
    Assert.That(reopened.Stat(fileId!.Value).Size, Is.EqualTo(large));
    using var fileHandle = reopened.OpenFile(fileId.Value, FileAccess.Read);
    var tail = new byte[1];
    Assert.That(fileHandle.Read(large - 1, tail), Is.EqualTo(1));
    Assert.That(tail[0], Is.EqualTo(0x5A));
  }

  [Test]
  public void Zip_PathPublicationPreservesUnchangedEntryAndArchiveComment() {
    var directory = NewDirectory();
    try {
      var path = Path.Combine(directory, "example.zip");
      using var created = CreateZip([
        ArchiveInputInfo.InMemory("untouched.txt", "keep me"u8.ToArray()),
        ArchiveInputInfo.InMemory("change.txt", "old"u8.ToArray()),
      ]);
      var bytes = created.ToArray();
      var comment = "archive-original-comment"u8.ToArray();
      BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2), checked((ushort)comment.Length));
      File.WriteAllBytes(path, [..bytes, ..comment]);

      using (var session = MountNamespaceResolver.OpenStagedArchivePath("Zip", path)) {
        var node = session.Lookup(session.RootNodeId, "change.txt");
        Assert.That(node, Is.Not.Null);
        using (var handle = session.OpenFile(node!.Value, FileAccess.ReadWrite)) {
          handle.SetLength(0);
          handle.Write(0, "updated"u8);
        }
        session.Flush();
        var newFile = session.CreateFile(session.RootNodeId, "added.txt");
        using (var handle = session.OpenFile(newFile, FileAccess.Write))
          handle.Write(0, "another"u8);
        session.Flush();
      }

      var after = File.ReadAllBytes(path);
      Assert.That(after.AsSpan(after.Length - comment.Length).SequenceEqual(comment), Is.True);
      using var file = File.OpenRead(path);
      var ops = FormatRegistry.GetArchiveOps("Zip")!;
      Assert.That(ops.ExtractEntryToMemory(file, "untouched.txt", null),
        Is.EqualTo("keep me"u8.ToArray()));
      Assert.That(ops.ExtractEntryToMemory(file, "change.txt", null),
        Is.EqualTo("updated"u8.ToArray()));
      Assert.That(ops.ExtractEntryToMemory(file, "added.txt", null),
        Is.EqualTo("another"u8.ToArray()));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public void Zip_StartupRecoversCorruptedPublicationFromVerifiedBackup() {
    var directory = NewDirectory();
    try {
      var path = Path.Combine(directory, "crashed.zip");
      using var created = CreateZip([ArchiveInputInfo.InMemory("a.txt", "before"u8.ToArray())]);
      var bytes = created.ToArray();
      var backupName = ".crashed.zip.recovery.backup";
      var candidateName = ".crashed.zip.recovery.candidate";
      File.WriteAllBytes(path, bytes);
      File.WriteAllBytes(Path.Combine(directory, backupName), bytes);
      File.WriteAllBytes(Path.Combine(directory, candidateName), [1, 2, 3]);
      File.WriteAllText(path + ".cwbmount.journal", string.Join("\n",
        "CWB-ARCHIVE-1",
        Convert.ToHexString(SHA256.HashData(bytes)),
        Convert.ToHexString(SHA256.HashData([1, 2, 3])),
        backupName,
        candidateName));
      File.WriteAllBytes(path, [9, 8, 7]);

      using (var mounted = MountNamespaceResolver.OpenStagedArchivePath("Zip", path)) {
        var node = mounted.Lookup(mounted.RootNodeId, "a.txt");
        Assert.That(node, Is.Not.Null);
      }

      Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
      Assert.Multiple(() => {
        Assert.That(File.Exists(path + ".cwbmount.journal"), Is.False);
        Assert.That(File.Exists(Path.Combine(directory, backupName)), Is.False);
        Assert.That(File.Exists(Path.Combine(directory, candidateName)), Is.False);
      });
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public void Zip_PathMountRefusesToOverwriteChangesMadeOutsideMount() {
    var directory = NewDirectory();
    try {
      var path = Path.Combine(directory, "external.zip");
      using var initial = CreateZip([ArchiveInputInfo.InMemory("a.txt", "initial"u8.ToArray())]);
      using var edited = CreateZip([ArchiveInputInfo.InMemory("a.txt", "external"u8.ToArray())]);
      File.WriteAllBytes(path, initial.ToArray());

      var mounted = MountNamespaceResolver.OpenStagedArchivePath("Zip", path);
      var id = mounted.CreateFile(mounted.RootNodeId, "local.txt");
      using (var handle = mounted.OpenFile(id, FileAccess.Write))
        handle.Write(0, "local"u8);

      File.WriteAllBytes(path, edited.ToArray());
      Assert.Throws<IOException>(() => mounted.Flush());
      Assert.Throws<IOException>(() => mounted.Dispose());
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(edited.ToArray()));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static MemoryStream CreateZip(IReadOnlyList<ArchiveInputInfo> inputs) {
    var creator = (IArchiveCreatable)FormatRegistry.GetArchiveOps("Zip")!;
    var result = new MemoryStream();
    creator.Create(result, inputs, new FormatCreateOptions { ForceCompress = true });
    result.Position = 0;
    return result;
  }

  private static string NewDirectory() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-mount-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }
}
