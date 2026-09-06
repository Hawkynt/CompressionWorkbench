#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;
using NUnit.Framework;

namespace Compression.Tests.Support;

/// <summary>
/// Builds the same conservative one-payload image for registry-wide maintenance
/// tests. The probe is identified by the SHA-256 of an entry's payload rather
/// than by its stored name, so single-stream formats and containers that assign
/// their own entry names are exercised too.
/// </summary>
/// <remarks>
/// A reader that renders its container rather than storing files (a disk image
/// rasterised into tracks, a transcoding media container, a message wrapper that
/// re-frames the bytes) cannot hand the planted payload back verbatim. That is a
/// property of the create/read path, not of the maintenance verb, so the probe
/// establishes it once — <see cref="ProbeImage.PayloadObservable"/> — instead of
/// assuming it. The verb itself is always required to execute.
/// </remarks>
internal static class MaintenanceOperationProbe {
  public const string ProbeName = "PROBE.BIN";

  private static readonly byte[] ProbeData = CreateProbeData();
  private static readonly string ProbeDigest = Digest(ProbeData);

  /// <summary>
  /// A freshly created single-payload image, plus whether the format's own reader
  /// hands the planted payload back byte-for-byte before any verb has run.
  /// </summary>
  internal sealed record ProbeImage(string Path, bool PayloadObservable);

  /// <summary>
  /// True when <paramref name="ops"/> declares — through the contract that exists
  /// for it — that the probe payload is not a legal input for this container.
  /// </summary>
  public static bool DeclinesProbeInput(IArchiveFormatOperations ops, out string reason) {
    reason = "";
    if (ops is not IArchiveWriteConstraints constraints) return false;
    if (constraints.CanAccept(new ArchiveInputInfo(ProbeName, ProbeName, false), out var declared)) return false;
    reason = declared ?? constraints.AcceptedInputsDescription;
    return true;
  }

  public static ProbeImage CreateImage(string formatId, string workDirectory) {
    Assert.That(Enum.TryParse<FormatDetector.Format>(formatId, out var format), Is.True,
      $"{formatId}: no FormatDetector.Format value exists for the registered format id.");

    var ops = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(ops, Is.Not.Null, $"{formatId}: registry exposes no archive operations.");
    if (DeclinesProbeInput(ops!, out var declined))
      Assert.Ignore($"{formatId}: declares this input inadmissible — {declined}");

    var sourcePath = Path.Combine(workDirectory, ProbeName);
    File.WriteAllBytes(sourcePath, ProbeData);

    var image = Path.Combine(workDirectory, "probe.img");
    try {
      ArchiveOperations.Create(image, [new ArchiveInput(sourcePath, ProbeName)],
        new CompressionOptions(), format, null);
    } catch (Exception ex) {
      Assert.Fail($"{formatId}: advertises a maintenance verb but neither creates the standard probe image "
        + $"nor declares the input inadmissible through IArchiveWriteConstraints: {ex.GetType().Name}: {ex.Message}");
    }

    Assert.That(File.Exists(image), Is.True, $"{formatId}: create returned without producing an image.");
    Assert.That(new FileInfo(image).Length, Is.GreaterThan(0), $"{formatId}: create produced an empty image.");

    using var stream = File.OpenRead(image);
    return new ProbeImage(image, ProbeEntries(ops!, stream).Count != 0);
  }

  /// <summary>
  /// Asserts the planted payload is still retrievable byte-for-byte. Only meaningful
  /// where the reader could retrieve it before the verb ran, which the caller carries
  /// in <see cref="ProbeImage.PayloadObservable"/>.
  /// </summary>
  public static void AssertProbeFiles(IArchiveFormatOperations ops, Stream image, string formatId) {
    var matches = ProbeEntries(ops, image);
    Assert.That(matches, Is.Not.Empty,
      $"{formatId}: the planted payload was retrievable before the operation and is gone after it; "
      + $"listed: {string.Join(", ", ListFiles(ops, image).Select(e => e.Name))}");
  }

  public static void AssertProbeFilesAbsent(IArchiveFormatOperations ops, Stream image, string formatId) {
    var matches = ProbeEntries(ops, image);
    Assert.That(matches, Is.Empty,
      $"{formatId}: purge returned successfully but the planted payload is still live as "
      + string.Join(", ", matches.Select(entry => entry.Name)));
  }

  public static IReadOnlyList<ArchiveEntryInfo> ListFiles(IArchiveFormatOperations ops, Stream image) {
    if (image.CanSeek) image.Position = 0;
    return ops.List(image, null).Where(entry => !entry.IsDirectory).ToArray();
  }

  private static IReadOnlyList<ArchiveEntryInfo> ProbeEntries(IArchiveFormatOperations ops, Stream image) {
    var matches = new List<ArchiveEntryInfo>();
    foreach (var entry in ListFiles(ops, image)) {
      try {
        if (image.CanSeek) image.Position = 0;
        using var payload = ops.OpenEntry(image, entry.Name, null);
        if (Digest(payload) == ProbeDigest)
          matches.Add(entry);
      } catch {
        // A rendered/synthetic candidate can be listable but not openable. If it
        // was the planted payload the digest will be missing and the caller fails.
      }
    }
    return matches;
  }

  private static byte[] CreateProbeData() {
    var result = new byte[4096];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 31 + 7);
    return result;
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
  private static string Digest(Stream data) => Convert.ToHexString(SHA256.HashData(data));
}
