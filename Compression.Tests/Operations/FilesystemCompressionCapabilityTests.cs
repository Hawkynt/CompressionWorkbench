#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.CramFs;
using FileSystem.SquashFs;

namespace Compression.Tests.Operations;

[TestFixture]
public class FilesystemCompressionCapabilityTests {
  [Test, Category("Architecture")]
  public void RegisteredFilesystemCompressionWriters_ExposeCompressionCapability() {
    FormatRegistration.EnsureInitialized();

    var offenders = FormatRegistry.All
      .OfType<ILayoutOptimizable>()
      .Where(layout => {
        var features = FilesystemOptimization.GetSupportedFeatures(layout);
        return features.HasFlag(FilesystemOptimizationFeatures.TransparentCompression)
               || features.HasFlag(FilesystemOptimizationFeatures.CompressionParameterSearch);
      })
      .Where(layout => layout is not ICompressionOptimizable)
      .Cast<IFormatDescriptor>()
      .Select(descriptor => descriptor.Id)
      .OrderBy(id => id, StringComparer.Ordinal)
      .ToArray();

    Assert.That(offenders, Is.Empty,
      "Filesystem writers with registered compression backends must expose ICompressionOptimizable: "
      + string.Join(", ", offenders));
  }

  [Test]
  public void AlwaysCompressedWriters_AcceptTransparentCompressionOption() {
    var cramfs = FilesystemOptimization.GetSupportedFeatures(new CramFsFormatDescriptor());
    var squashfs = FilesystemOptimization.GetSupportedFeatures(new SquashFsFormatDescriptor());

    Assert.Multiple(() => {
      Assert.That(cramfs.HasFlag(FilesystemOptimizationFeatures.TransparentCompression), Is.True);
      Assert.That(squashfs.HasFlag(FilesystemOptimizationFeatures.TransparentCompression), Is.True);
      Assert.That(squashfs.HasFlag(FilesystemOptimizationFeatures.CompressionParameterSearch), Is.True,
        "SquashFS publishes multiple data block sizes that affect its compressed representation");
      Assert.That(OptimizationCapabilities.CanCompress(new CramFsFormatDescriptor()), Is.True);
      Assert.That(OptimizationCapabilities.CanCompress(new SquashFsFormatDescriptor()), Is.True);
    });
  }
}
