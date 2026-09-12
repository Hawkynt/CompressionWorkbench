using FileFormat.Arc;

namespace Compression.Tests.Arc;

[TestFixture]
public class ArcCompatibilityTests {
  [Test]
  public void SeaProfiles_ExposeMethodsAtHistoricalIntroductionPoints() {
    Assert.Multiple(() => {
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc2, ArcCompressionMethod.Stored), Is.True);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc2, ArcCompressionMethod.Squeezed), Is.True);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc2, ArcCompressionMethod.Crunched5), Is.False);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc40, ArcCompressionMethod.Crunched5), Is.True);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc40, ArcCompressionMethod.Crunched6), Is.False);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc41, ArcCompressionMethod.Crunched6), Is.True);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc41, ArcCompressionMethod.Crunched7), Is.False);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc46, ArcCompressionMethod.Crunched7), Is.True);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc46, ArcCompressionMethod.Crunched), Is.False);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc50, ArcCompressionMethod.Crunched), Is.True);
      Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.SeaArc50, ArcCompressionMethod.Squashed), Is.False);
    });
  }

  [Test]
  public void PkArcProfile_AllowsSquashedMethod9() {
    Assert.That(ArcCompatibility.IsSupported(ArcCompatibilityProfile.PkArc, ArcCompressionMethod.Squashed), Is.True);
  }

  [Test]
  public void InvalidProfile_IsNeverTreatedAsCompatible() {
    var invalid = (ArcCompatibilityProfile)byte.MaxValue;

    Assert.Multiple(() => {
      Assert.That(ArcCompatibility.IsSupported(invalid, ArcCompressionMethod.Stored), Is.False);
      Assert.That(
        () => ArcCompatibility.EnsureSupported(invalid, ArcCompressionMethod.Stored),
        Throws.TypeOf<ArgumentOutOfRangeException>());
    });
  }

  [Test]
  public void Writer_RejectsMethodAboveProfile() {
    using var stream = new MemoryStream();
    using var writer = new ArcWriter(
      stream,
      leaveOpen: true,
      compatibilityProfile: ArcCompatibilityProfile.SeaArc2);

    Assert.That(
      () => writer.AddEntry("TEST.DAT", new byte[4096], ArcCompressionMethod.Crunched5),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Writer_EmitsPkArcSquashedMethod() {
    using var stream = new MemoryStream();
    using (var writer = new ArcWriter(
      stream,
      leaveOpen: true,
      compatibilityProfile: ArcCompatibilityProfile.PkArc)) {
      writer.AddEntry("TEST.DAT", new byte[4096], ArcCompressionMethod.Squashed);
      writer.Finish();
    }

    var bytes = stream.ToArray();
    Assert.Multiple(() => {
      Assert.That(bytes[0], Is.EqualTo(0x1A));
      Assert.That(bytes[1], Is.EqualTo((byte)ArcCompressionMethod.Squashed));
    });
  }

  [Test]
  public void Writer_ProfileChecksEmittedFallbackMethod() {
    using var stream = new MemoryStream();
    using (var writer = new ArcWriter(
      stream,
      leaveOpen: true,
      compatibilityProfile: ArcCompatibilityProfile.SeaArc2)) {
      writer.AddEntry("TINY.DAT", [0x42], ArcCompressionMethod.Crunched);
      writer.Finish();
    }

    Assert.That(stream.ToArray()[1], Is.EqualTo((byte)ArcCompressionMethod.Stored));
  }
}
