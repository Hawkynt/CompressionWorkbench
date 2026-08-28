#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public sealed class RebuildProgressCancellationTests {
  [Test]
  public void RebuildInPlace_CancelDuringTargetWrite_LeavesOriginalUntouched() {
    var original = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
    using var archive = new MemoryStream();
    archive.Write(original);
    archive.Position = 0;

    var descriptor = new FakeDescriptor();
    using var cts = new CancellationTokenSource();
    var phases = new List<string>();

    Assert.Throws<OperationCanceledException>(() =>
      RebuildVerb.RebuildInPlace(archive, descriptor, descriptor,
        onProgress: e => {
          phases.Add(e.Phase);
          if (e.Phase == "writing" && e.CurrentWriteOffset > 0)
            cts.Cancel();
        }, cancellationToken: cts.Token));

    Assert.That(phases, Does.Contain("scanning"));
    Assert.That(phases, Does.Contain("reading"));
    Assert.That(phases, Does.Contain("writing"));
    Assert.That(phases, Does.Not.Contain("committing"));
    Assert.That(archive.ToArray(), Is.EqualTo(original),
      "A cancelled staged rebuild must never overwrite the source stream.");
  }

  [Test]
  public void RebuildInPlace_ReportsColoredTargetAndCommitPhases() {
    using var archive = new MemoryStream();
    archive.Write(new byte[4096]);
    archive.Position = 0;
    var descriptor = new FakeDescriptor();
    var events = new List<DefragProgressEvent>();

    RebuildVerb.RebuildInPlace(archive, descriptor, descriptor, onProgress: events.Add);

    Assert.That(events.Select(e => e.Phase), Does.Contain("writing"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("verifying"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("staged"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("committing"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("complete"));

    var targetMap = events.First(e => e.Phase == "writing").BlockMap;
    Assert.That(targetMap, Is.Not.Null.And.Not.Empty);
    Assert.That(targetMap!.Any(b => b.Kind == DefragBlockKind.Used && b.Classification.HasValue), Is.True,
      "Staged archive rebuilds should keep a colored block map visible while writing.");
  }

  private sealed class FakeDescriptor : IArchiveFormatOperations, IArchiveCreatable {
    private static readonly byte[] Payload = Enumerable.Range(0, 256 * 1024)
      .Select(i => (byte)(i * 17)).ToArray();

    public List<ArchiveEntryInfo> List(Stream stream, string? password)
      => [new ArchiveEntryInfo(0, "payload.bin", Payload.Length, Payload.Length,
        "deflate", false, false, null)];

    public void Extract(Stream stream, string outputDir, string? password, string[]? files)
      => File.WriteAllBytes(Path.Combine(outputDir, "payload.bin"), Payload);

    public Stream OpenEntry(Stream archive, string entryName, string? password)
      => new MemoryStream(Payload, writable: false);

    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
      var data = inputs.Single(i => !i.IsDirectory).ReadContent();
      const int ChunkSize = 16 * 1024;
      for (var offset = 0; offset < data.Length; offset += ChunkSize)
        output.Write(data, offset, Math.Min(ChunkSize, data.Length - offset));
    }
  }
}
