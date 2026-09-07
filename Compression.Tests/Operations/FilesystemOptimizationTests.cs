#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ext;
using FileSystem.Ntfs;

namespace Compression.Tests.Operations;

[TestFixture]
public class FilesystemOptimizationTests {
  [Test]
  public void SupportedFeatures_AreDerivedFromWriterCapabilities() {
    var ext = FilesystemOptimization.GetSupportedFeatures(new ExtFormatDescriptor());
    Assert.Multiple(() => {
      Assert.That(ext.HasFlag(FilesystemOptimizationFeatures.SparseFiles), Is.True);
      Assert.That(ext.HasFlag(FilesystemOptimizationFeatures.HardLinkDeduplication), Is.True);
      Assert.That(ext.HasFlag(FilesystemOptimizationFeatures.SymbolicLinkDeduplication), Is.False,
        "reading symlinks is not enough: the ext writer does not yet create them during deduplication");
      Assert.That(ext.HasFlag(FilesystemOptimizationFeatures.TransparentCompression), Is.False);
    });

    var ntfs = FilesystemOptimization.GetSupportedFeatures(new NtfsFormatDescriptor());
    Assert.Multiple(() => {
      Assert.That(ntfs.HasFlag(FilesystemOptimizationFeatures.TransparentCompression), Is.True,
        "NTFS publishes the writer-honoured Compression=Off/LZNT1 schema axis");
      Assert.That(ntfs.HasFlag(FilesystemOptimizationFeatures.CompressionParameterSearch), Is.True);
    });
  }

  [Test]
  public void UnsupportedSymbolicLinkDeduplication_IsRejectedBeforeWriting() {
    var descriptor = new ExtFormatDescriptor();
    var options = new FilesystemOptimizationOptions { DeduplicateWithSymbolicLinks = true };

    Assert.That(
      () => FilesystemOptimization.ValidateRequested(descriptor, options),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Optimize_ProbesCompressionAxis_AndKeepsSmallestCandidate() {
    var descriptor = new ProbeFilesystem();
    using var input = new MemoryStream(new byte[200]);
    using var output = new MemoryStream();
    var progress = new List<(int Done, int Total)>();

    FilesystemOptimization.Optimize(descriptor, input, output, new FilesystemOptimizationOptions {
      TryCompressionParameters = true,
      MaxCompressionProbes = 8,
      OnProbeProgress = (done, total) => progress.Add((done, total)),
    });

    Assert.Multiple(() => {
      Assert.That(output.Length, Is.EqualTo(40), "LZ should beat the 120-byte uncompressed probe");
      Assert.That(descriptor.SeenCompression, Does.Contain("Off"));
      Assert.That(descriptor.SeenCompression, Does.Contain("LZ"));
      Assert.That(progress[^1], Is.EqualTo((2, 2)));
    });
  }

  [Test]
  public void Optimize_TransparentCompression_EnablesWriterCompressionWithoutParameterSearch() {
    var descriptor = new ProbeFilesystem();
    using var input = new MemoryStream(new byte[200]);
    using var output = new MemoryStream();

    FilesystemOptimization.Optimize(descriptor, input, output, new FilesystemOptimizationOptions {
      UseTransparentCompression = true,
    });

    Assert.Multiple(() => {
      Assert.That(output.Length, Is.EqualTo(40));
      Assert.That(descriptor.SeenCompression, Is.EqualTo(new[] { "LZ" }));
    });
  }

  [Test]
  public void Shrink_WithAdvancedOptions_UsesTheSameLayoutOptimizationPipeline() {
    IArchiveShrinkable descriptor = new ProbeFilesystem();
    using var input = new MemoryStream(new byte[200]);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output, new FilesystemOptimizationOptions {
      MakeSparse = true,
      DeduplicateWithHardLinks = true,
      TryCompressionParameters = true,
    });

    Assert.That(output.Length, Is.EqualTo(20),
      "sparse + hard-link reclaim should compose with the best compression probe");
  }

  [Test]
  public void Shrink_WithoutAdvancedOptions_PreservesTheExistingShrinkDispatch() {
    IArchiveShrinkable descriptor = new ProbeFilesystem();
    using var input = new MemoryStream(new byte[200]);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output, new FilesystemOptimizationOptions());

    Assert.That(output.Length, Is.EqualTo(90),
      "an empty option set must still call a format's native/canonical Shrink override");
  }

  private sealed class ProbeFilesystem : IArchiveShrinkable, ILayoutOptimizable, IFormatOptionsSchema {
    public List<string> SeenCompression { get; } = [];

    public LayoutReclaim ReclaimSupport => LayoutReclaim.Sparse | LayoutReclaim.HardLinks;

    public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
      new(
        Key: "Compression",
        DisplayName: "Compression",
        Kind: FormatOptionKind.Enum,
        Default: "Off",
        AllowedValues: ["Off", "LZ"]),
    ];

    public void Shrink(Stream input, Stream output) {
      output.SetLength(0);
      output.Write(new byte[90]);
    }

    public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
      var compression = options.Parameters != null
        && options.Parameters.TryGetValue("Compression", out var value)
          ? value
          : "Off";
      SeenCompression.Add(compression);

      var length = compression == "LZ" ? 40 : 120;
      if (options.MakeSparse) length -= 10;
      if (options.DeduplicateWithLinks) length -= 10;

      target.Position = 0;
      target.SetLength(0);
      target.Write(new byte[length]);
    }
  }
}
