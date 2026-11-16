using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Mounting;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class JournaledArchiveWritebackTests {
  [Test]
  public void FailedNativeMutationRestoresOriginalBytesAndDeletesJournal() {
    WithImage((path, image, original) => {
      Assert.Throws<IOException>(() => JournaledArchiveWriteback.Apply(
        image,
        target => {
          target.Position = 0;
          target.Write("partial mutation"u8);
          target.SetLength(16);
          throw new IOException("simulated native editor failure");
        },
        _ => Assert.Fail("Validation must not run after a failed mutation.")));

      Assert.Multiple(() => {
        Assert.That(File.Exists(JournaledArchiveWriteback.JournalPath(path)), Is.False);
        Assert.That(ReadBytes(image), Is.EqualTo(original));
      });
    });
  }

  [Test]
  public void FailedVerificationRestoresSourceAndDoesNotPublishCorruptContent() {
    WithImage((path, image, original) => {
      Assert.Throws<InvalidDataException>(() => JournaledArchiveWriteback.Apply(
        image,
        target => {
          target.Position = 0;
          target.Write("new contents"u8);
          target.SetLength(12);
        },
        _ => throw new InvalidDataException("semantic output changed unexpectedly")));

      Assert.Multiple(() => {
        Assert.That(File.Exists(JournaledArchiveWriteback.JournalPath(path)), Is.False);
        Assert.That(ReadBytes(image), Is.EqualTo(original));
      });
    });
  }

  [Test]
  public void VerifiedNativeMutationPersistsWithoutLeavingUndoJournal() {
    WithImage((path, image, _) => {
      JournaledArchiveWriteback.Apply(image,
        target => {
          target.Position = 0;
          target.Write("valid edit"u8);
          target.SetLength(10);
        },
        target => {
          target.Position = 0;
          Span<byte> buffer = stackalloc byte[10];
          target.ReadExactly(buffer);
          Assert.That(buffer.ToArray(), Is.EqualTo("valid edit"u8.ToArray()));
        });

      Assert.Multiple(() => {
        Assert.That(File.Exists(JournaledArchiveWriteback.JournalPath(path)), Is.False);
        Assert.That(ReadBytes(image), Is.EqualTo("valid edit"u8.ToArray()));
      });
    });
  }

  [Test]
  public void RestartReplaysSurvivingValidJournalAfterInterruptedLiveEdit() {
    WithImage((path, image, original) => {
      var journal = JournaledArchiveWriteback.JournalPath(path);
      WriteTestJournal(journal, original);
      image.Position = 0;
      image.Write("power loss after beginning change"u8);
      image.SetLength(33);
      image.Flush(flushToDisk: true);

      Assert.That(JournaledArchiveWriteback.RecoverIfRequired(image), Is.True);
      Assert.Multiple(() => {
        Assert.That(File.Exists(journal), Is.False);
        Assert.That(ReadBytes(image), Is.EqualTo(original));
        Assert.That(JournaledArchiveWriteback.RecoverIfRequired(image), Is.False);
      });
    });
  }

  [Test]
  public void DamagedUndoJournalFailsClosedWithoutOverwritingLiveArchive() {
    WithImage((path, image, original) => {
      var journal = JournaledArchiveWriteback.JournalPath(path);
      WriteTestJournal(journal, original);
      using (var damaged = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
        damaged.Position = damaged.Length - 1;
        damaged.WriteByte(0x55);
      }

      Assert.Throws<InvalidDataException>(() => JournaledArchiveWriteback.RecoverIfRequired(image));
      Assert.Multiple(() => {
        Assert.That(File.Exists(journal), Is.True, "A corrupt journal must remain for manual recovery.");
        Assert.That(ReadBytes(image), Is.EqualTo(original));
      });
      File.Delete(journal);
    });
  }

  private static void WriteTestJournal(string path, byte[] original) {
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    file.Write("CWBUNDO1"u8);
    Span<byte> length = stackalloc byte[sizeof(long)];
    BinaryPrimitives.WriteInt64LittleEndian(length, original.Length);
    file.Write(length);
    file.Write(SHA256.HashData(original));
    file.Write(original);
    file.Flush(flushToDisk: true);
  }

  private static byte[] ReadBytes(FileStream source) {
    source.Position = 0;
    using var result = new MemoryStream();
    source.CopyTo(result);
    return result.ToArray();
  }

  private static void WithImage(Action<string, FileStream, byte[]> verify) {
    var folder = Path.Combine(Path.GetTempPath(), $"cwb-journal-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    try {
      var path = Path.Combine(folder, "source.zip");
      var original = "original archive bytes - immutable"u8.ToArray();
      File.WriteAllBytes(path, original);
      using var source = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
        FileShare.Read, 4096, FileOptions.None);
      verify(path, source, original);
    } finally {
      Directory.Delete(folder, recursive: true);
    }
  }
}
