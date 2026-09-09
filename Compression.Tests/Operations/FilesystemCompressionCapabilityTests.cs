#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.CramFs;
using FileSystem.SquashFs;

namespace Compression.Tests.Operations;

[TestFixture]
public class FilesystemCompressionCapabilityTests {
  [Test]
  public void AlwaysCompressedWriters_AcceptTransparentCompressionOption() {
    var cramfs = FilesystemOptimization.GetSupportedFeatures(new CramFsFormatDescriptor());
    var squashfs = FilesystemOptimization.GetSupportedFeatures(new SquashFsFormatDescriptor());

    Assert.Multiple(() => {
      Assert.That(cramfs.HasFlag(FilesystemOptimizationFeatures.TransparentCompression), Is.True);
      Assert.That(squashfs.HasFlag(FilesystemOptimizationFeatures.TransparentCompression), Is.True);
      Assert.That(squashfs.HasFlag(FilesystemOptimizationFeatures.CompressionParameterSearch), Is.True,
        "SquashFS publishes multiple data block sizes that affect its compressed representation");
    });
  }
}
