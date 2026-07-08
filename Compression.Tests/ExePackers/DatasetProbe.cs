using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using Compression.Tests.Support;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Explicit probe (not part of the normal suite) that runs the registered
/// executable-packer handlers against the packing-box dataset. Point
/// <c>CWB_DATASET</c> at a checkout's <c>packed</c> directory, or set
/// <c>CWB_DOWNLOAD_EXE_PACKER_TOOLS=1</c> to download the public GitHub archive,
/// and run:
/// <c>dotnet test --filter FullyQualifiedName~DatasetProbe.Probe</c>.
/// </summary>
[TestFixture, Explicit]
public class DatasetProbe {

  [Test]
  public void Probe() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    Assert.That(Directory.Exists(root!), $"Dataset packed root '{root}' does not exist.");

    var sampleLimit = ProbeSampleLimit();
    var sb = new StringBuilder();
    var json = new StringBuilder();
    json.AppendLine("[");
    var firstPacker = true;
    foreach (var dir in Directory.EnumerateDirectories(root!).OrderBy(d => d)) {
      var packer = Path.GetFileName(dir);
      var files = Directory.EnumerateFiles(dir).Take(sampleLimit).ToList();
      var levels = new Dictionary<ExecutableUnpackLevel, int>();
      var handlerHits = new Dictionary<string, int>();
      var errors = 0;
      foreach (var f in files) {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(f); } catch { errors++; continue; }
        try {
          var match = ExecutablePackerHandlers.DetectBest(bytes);
          if (match == null) { Bump(handlerHits, "(none)"); continue; }
          Bump(handlerHits, match.Handler.Id);
          var packed = match.Handler.Parse(bytes, match.Detection);
          var result = match.Handler.Unpack(packed, new());
          Bump(levels, result.Level);
        } catch { errors++; }
      }
      var best = levels.Keys.Count > 0 ? levels.Keys.Max() : ExecutableUnpackLevel.DetectionOnly;
      var decompressed = levels.Where(kv => kv.Key >= ExecutableUnpackLevel.PayloadDecompressed).Sum(kv => kv.Value);
      var hits = string.Join(",", handlerHits.OrderByDescending(k => k.Value).Select(k => $"{k.Key}:{k.Value}"));
      sb.AppendLine($"{packer,-22} n={files.Count,3} decompressed={decompressed,3} best={best,-24} handlers=[{hits}] err={errors}");

      if (!firstPacker) json.AppendLine(",");
      firstPacker = false;
      json.Append("  { ");
      json.Append($"\"packer\": \"{EscapeJson(packer)}\", ");
      json.Append($"\"files\": {files.Count}, ");
      json.Append($"\"decompressed\": {decompressed}, ");
      json.Append($"\"bestLevel\": \"{best}\", ");
      json.Append($"\"errors\": {errors}, ");
      json.Append("\"handlers\": { ");
      var firstHandler = true;
      foreach (var (handler, count) in handlerHits.OrderBy(k => k.Key)) {
        if (!firstHandler) json.Append(", ");
        firstHandler = false;
        json.Append($"\"{EscapeJson(handler)}\": {count}");
      }
      json.Append(" } }");
    }
    json.AppendLine();
    json.AppendLine("]");
    TestContext.Out.Write(sb.ToString());
    TestContext.Out.Write(json.ToString());
    TestContext.Out.Flush();
  }

  [Test]
  public void DatasetArchive_IsFetchableAndHasPackedFamilies() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    var packers = Directory.EnumerateDirectories(root!).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.Multiple(() => {
      Assert.That(packers.Contains("upx"), Is.True);
      Assert.That(packers.Contains("fsg"), Is.True);
      Assert.That(packers.Contains("aspack"), Is.True);
    });
  }

  private static void Bump<T>(Dictionary<T, int> d, T k, int add = 1) where T : notnull =>
    d[k] = d.TryGetValue(k, out var v) ? v + add : add;

  private static string EscapeJson(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

  private static int ProbeSampleLimit() {
    var value = Environment.GetEnvironmentVariable("CWB_DATASET_LIMIT");
    return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 5;
  }
}
