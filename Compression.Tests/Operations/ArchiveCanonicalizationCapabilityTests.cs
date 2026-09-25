using Compression.Registry;
using FileFormat.Tar;

namespace Compression.Tests.Operations;

[TestFixture]
public class ArchiveCanonicalizationCapabilityTests {
  [Test, Category("RoundTrip")]
  public void Tar_Canonicalize_IsSemanticPreservingAndByteIdempotent() {
    var descriptor = new TarFormatDescriptor();
    var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_714_973_290);

    using var source = new MemoryStream();
    using (var writer = new TarWriter(source, leaveOpen: true, format: TarHeaderFormat.Gnu, blockingFactor: 20)) {
      writer.AddEntry(new TarEntry {
        Name = "payload.txt",
        TypeFlag = TarConstants.TypeRegular,
        Mode = 0x1A4,
        Uid = 1001,
        Gid = 1002,
        UserName = "alice",
        GroupName = "staff",
        ModifiedTime = timestamp,
      }, Enumerable.Repeat((byte)'A', 4096).ToArray());

      writer.AddEntry(new TarEntry {
        Name = "link",
        TypeFlag = TarConstants.TypeSymLink,
        LinkName = "payload.txt",
        Mode = 0x1FF,
        Uid = 1001,
        Gid = 1002,
        UserName = "alice",
        GroupName = "staff",
        ModifiedTime = timestamp,
      }, []);
      writer.Finish();
    }

    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, descriptor);
    source.Position = 0;

    using var canonical = new MemoryStream();
    ((IArchiveCanonicalizable)descriptor).Canonicalize(source, canonical);

    canonical.Position = 0;
    var after = SemanticPreservationManifest.Capture(canonical, descriptor);
    before.VerifyEquivalent(after);

    canonical.Position = 0;
    using var canonicalAgain = new MemoryStream();
    ((IArchiveCanonicalizable)descriptor).Canonicalize(canonical, canonicalAgain);

    Assert.That(canonicalAgain.ToArray(), Is.EqualTo(canonical.ToArray()),
      "canonical TAR output must be a stable byte representation");
  }
}
