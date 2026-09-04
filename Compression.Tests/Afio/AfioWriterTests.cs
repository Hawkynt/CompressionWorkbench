#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Afio;

namespace Compression.Tests.Afio;

/// <summary>
/// afio could be listed and extracted but not written, even though its member format is the
/// portable-ASCII cpio header the repo already writes for cpio and its layout is fully documented.
/// These tests pin the header bytes against the published field order rather than against the
/// reader, so a writer that agrees with a buggy reader still fails.
/// </summary>
[TestFixture]
public sealed class AfioWriterTests {

  private const int HeaderSize = 76;
  private static readonly byte[] Alpha = "alpha member payload"u8.ToArray();
  private static readonly byte[] Beta = "beta"u8.ToArray();

  private static AfioFormatDescriptor Descriptor => new();

  // ExtractEntryToMemory is a default interface method, so it is only reachable through the
  // interface reference.
  private static IArchiveFormatOperations Ops => new AfioFormatDescriptor();

  private static long Octal(byte[] buffer, int offset, int length) {
    var text = Encoding.ASCII.GetString(buffer, offset, length).Trim();
    return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
  }

  [Test, Category("HappyPath")]
  public void DescriptorAdvertisesCreate() {
    var d = Descriptor;
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  /// <summary>
  /// The odc header is eleven fixed-width octal fields at fixed offsets: magic(6) at 0, dev(6) at
  /// 6, ino(6) at 12, mode(6) at 18, uid(6) at 24, gid(6) at 30, nlink(6) at 36, rdev(6) at 42,
  /// mtime(11) at 48, namesize(6) at 59, filesize(11) at 65 — 76 bytes, no padding, and namesize
  /// counts the NUL.
  /// </summary>
  [Test, Category("Spec")]
  public void TheHeaderMatchesThePortableAsciiFieldLayout() {
    using var ms = new MemoryStream();
    AfioWriter.WriteFile(ms, "alpha.txt", Alpha);
    var bytes = ms.ToArray();

    Assert.That(Encoding.ASCII.GetString(bytes, 0, 6), Is.EqualTo("070707"));
    Assert.Multiple(() => {
      Assert.That(Octal(bytes, 18, 6), Is.EqualTo(AfioWriter.RegularFileMode), "mode");
      Assert.That(Octal(bytes, 36, 6), Is.EqualTo(1), "nlink");
      Assert.That(Octal(bytes, 59, 6), Is.EqualTo("alpha.txt".Length + 1), "namesize counts the NUL");
      Assert.That(Octal(bytes, 65, 11), Is.EqualTo(Alpha.Length), "filesize");
    });

    // Name follows the header, NUL-terminated, and the payload follows the name with no padding.
    Assert.That(Encoding.ASCII.GetString(bytes, HeaderSize, 9), Is.EqualTo("alpha.txt"));
    Assert.That(bytes[HeaderSize + 9], Is.EqualTo(0));
    Assert.That(bytes.Skip(HeaderSize + 10).Take(Alpha.Length).ToArray(), Is.EqualTo(Alpha));
    Assert.That(bytes, Has.Length.EqualTo(HeaderSize + 10 + Alpha.Length),
      "the portable-ASCII format has no alignment padding anywhere");
  }

  [Test, Category("Spec")]
  public void ADirectoryMemberCarriesTheDirectoryModeBit() {
    using var ms = new MemoryStream();
    AfioWriter.WriteDirectory(ms, "sub/");
    var bytes = ms.ToArray();

    var mode = Octal(bytes, 18, 6);
    Assert.That(mode & 0xF000, Is.EqualTo(0x4000), "S_IFDIR must be set or the reader sees a file");
    Assert.That(Octal(bytes, 65, 11), Is.EqualTo(0), "a directory member has no payload");
    Assert.That(Encoding.ASCII.GetString(bytes, HeaderSize, 3), Is.EqualTo("sub"),
      "the trailing slash is not part of the stored name");
  }

  [Test, Category("Spec")]
  public void TheArchiveEndsWithTheTrailerMember() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [ArchiveInputInfo.InMemory("alpha.txt", Alpha)], new FormatCreateOptions());
    var bytes = ms.ToArray();

    var trailerHeader = bytes.Length - HeaderSize - 11;
    Assert.That(Encoding.ASCII.GetString(bytes, trailerHeader, 6), Is.EqualTo("070707"));
    Assert.That(Encoding.ASCII.GetString(bytes, trailerHeader + HeaderSize, 10), Is.EqualTo("TRAILER!!!"));
  }

  [Test, Category("RoundTrip")]
  public void CreateRoundTripsThroughTheDescriptorsOwnReader() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [
      ArchiveInputInfo.InMemory("alpha.txt", Alpha),
      new ArchiveInputInfo("sub", "sub", IsDirectory: true),
      ArchiveInputInfo.InMemory("sub/beta.bin", Beta),
    ], new FormatCreateOptions());

    ms.Position = 0;
    var entries = Descriptor.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "alpha.txt", "sub", "sub/beta.bin" }));
    Assert.That(entries.Single(e => e.Name == "sub").IsDirectory, Is.True);
    Assert.That(entries.Single(e => e.Name == "alpha.txt").Method, Is.EqualTo("Stored"));

    Assert.Multiple(() => {
      Assert.That(Ops.ExtractEntryToMemory(ms, "alpha.txt", null), Is.EqualTo(Alpha));
      Assert.That(Ops.ExtractEntryToMemory(ms, "sub/beta.bin", null), Is.EqualTo(Beta));
    });
  }

  [Test, Category("RoundTrip")]
  public void AnEmptyMemberSurvives() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [ArchiveInputInfo.InMemory("empty.bin", [])], new FormatCreateOptions());
    ms.Position = 0;

    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "empty.bin" }));
    Assert.That(Ops.ExtractEntryToMemory(ms, "empty.bin", null), Is.Empty);
  }

  /// <summary>
  /// The reader recognises a compressed member by sniffing <c>1F 8B</c> at the start of the
  /// payload, so storing content that already looks like gzip would come back inflated — different
  /// bytes than went in. Refusing is the honest answer; silently storing it is not.
  /// </summary>
  [Test, Category("Sad")]
  public void APayloadThatLooksLikeGzipIsRefusedRatherThanSilentlyMangled() {
    var gzipish = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 };
    using var ms = new MemoryStream();
    var ex = Assert.Throws<NotSupportedException>(() =>
      Descriptor.Create(ms, [ArchiveInputInfo.InMemory("payload.gz", gzipish)], new FormatCreateOptions()));
    Assert.That(ex!.Message, Does.Contain("gzip signature"));
  }
}
