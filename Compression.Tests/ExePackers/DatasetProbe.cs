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

  [Test]
  public void PackingBoxPackersManifest_IsFetchableAndAuditsRegisteredHandlers() {
    var manifest = ExecutablePackerToolCache.GetPackingBoxPackersManifest();
    Assert.That(manifest, Is.Not.Null,
      "Set CWB_PACKING_BOX_PACKERS_YML to src/conf/packers.yml or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");

    var packers = ParsePackingBoxPackerNames(manifest!);
    var handlerIds = ExecutablePackerHandlers.All.Select(h => h.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var expected = PackingBoxHandlerAliases();
    var covered = packers.Where(p => expected.TryGetValue(p, out var id) && handlerIds.Contains(id)).OrderBy(p => p).ToList();
    var mappedButMissing = packers.Where(p => expected.TryGetValue(p, out var id) && !handlerIds.Contains(id)).OrderBy(p => p).ToList();
    var unmapped = packers.Where(p => !expected.ContainsKey(p)).OrderBy(p => p).ToList();

    TestContext.Out.WriteLine($"Packing Box packers: {packers.Count}");
    TestContext.Out.WriteLine($"Mapped to registered CW handlers: {covered.Count}");
    TestContext.Out.WriteLine($"Mapped aliases missing handlers: {string.Join(", ", mappedButMissing)}");
    TestContext.Out.WriteLine($"Unmapped manifest packers: {string.Join(", ", unmapped)}");

    Assert.Multiple(() => {
      Assert.That(packers.Count, Is.GreaterThan(80));
      Assert.That(packers, Does.Contain("UPX"));
      Assert.That(packers, Does.Contain("Crinkler"));
      Assert.That(packers, Does.Contain("PyPePacker"));
      Assert.That(covered, Does.Contain("UPX"));
      Assert.That(covered, Does.Contain("GZEXE"));
      Assert.That(covered, Does.Contain("Papaw"));
      Assert.That(mappedButMissing, Is.Empty);
    });
  }

  [Test, Category("ExternalTool")]
  public void PackingBoxFirstSample_EachFamily_UsesExpectedHandler() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");

    var cases = new (string Family, string HandlerId, ExecutableUnpackLevel MinimumLevel)[] {
      ("Alienyze", "alienyze", ExecutableUnpackLevel.PayloadLocated),
      ("Amber", "amber", ExecutableUnpackLevel.PayloadLocated),
      ("ASPack", "aspack", ExecutableUnpackLevel.PayloadLocated),
      ("BeRoEXEPacker", "beroexepacker", ExecutableUnpackLevel.PayloadLocated),
      ("Enigma Virtual Box", "enigmavirtualbox", ExecutableUnpackLevel.PayloadLocated),
      ("Eronana Packer", "eronanapacker", ExecutableUnpackLevel.PayloadLocated),
      ("Exe32pack", "exe32pack", ExecutableUnpackLevel.PayloadLocated),
      ("EXpressor", "expressor", ExecutableUnpackLevel.PayloadLocated),
      ("FSG", "fsg", ExecutableUnpackLevel.PayloadLocated),
      ("JDPack", "jdpack", ExecutableUnpackLevel.PayloadLocated),
      ("MEW", "mew", ExecutableUnpackLevel.PayloadLocated),
      ("Molebox", "molebox", ExecutableUnpackLevel.PayloadLocated),
      ("MPRESS", "mpress", ExecutableUnpackLevel.PayloadLocated),
      ("Neolite", "neolite", ExecutableUnpackLevel.PayloadLocated),
      ("NSPack", "nspack", ExecutableUnpackLevel.PayloadLocated),
      ("Packman", "packman", ExecutableUnpackLevel.PayloadLocated),
      ("PECompact", "pecompact", ExecutableUnpackLevel.PayloadLocated),
      ("PEtite", "petite", ExecutableUnpackLevel.PayloadLocated),
      ("RLPack", "rlpack", ExecutableUnpackLevel.PayloadDecompressed),
      ("TELock", "telock", ExecutableUnpackLevel.PayloadLocated),
      ("Themida", "themida", ExecutableUnpackLevel.PayloadLocated),
      ("UPX", "upx", ExecutableUnpackLevel.PayloadLocated),
      ("WinUpack", "winupack", ExecutableUnpackLevel.PayloadLocated),
      ("Yoda-Crypter", "yodacrypter", ExecutableUnpackLevel.PayloadLocated),
      ("Yoda-Protector", "yodaprotector", ExecutableUnpackLevel.PayloadLocated),
    };

    foreach (var (family, handlerId, minimumLevel) in cases) {
      var sample = Directory.EnumerateFiles(Path.Combine(root!, family)).OrderBy(Path.GetFileName).FirstOrDefault();
      Assert.That(sample, Is.Not.Null, family);

      var bytes = File.ReadAllBytes(sample!);
      var match = ExecutablePackerHandlers.DetectBest(bytes);
      Assert.That(match, Is.Not.Null, $"{family}: {Path.GetFileName(sample)}");

      var packed = match!.Handler.Parse(bytes, match.Detection);
      var result = match.Handler.Unpack(packed, new());
      var artifacts = string.Join(", ", result.Artifacts.Select(a => a.Name));
      Assert.Multiple(() => {
        Assert.That(match.Handler.Id, Is.EqualTo(handlerId), $"{family}: {Path.GetFileName(sample)}");
        Assert.That(result.Level, Is.GreaterThanOrEqualTo(minimumLevel),
          $"{family}: {Path.GetFileName(sample)} artifacts={artifacts}");
        Assert.That(HasPayloadArtifact(result), Is.True,
          $"{family}: {Path.GetFileName(sample)} artifacts={artifacts}");
      });
    }
  }

  [Test]
  public void FsgAccessChk_AtLeastLocatesPayload() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    var sample = Path.Combine(root!, "FSG", "fsg_accesschk.exe");
    Assert.That(File.Exists(sample), $"Expected Packing Box sample '{sample}'.");

    var bytes = File.ReadAllBytes(sample);
    var match = ExecutablePackerHandlers.DetectBest(bytes);
    Assert.That(match, Is.Not.Null);

    var packed = match!.Handler.Parse(bytes, match.Detection);
    var result = match.Handler.Unpack(packed, new());
    var artifacts = string.Join(", ", result.Artifacts.Select(a => a.Name));
    var diagnostics = string.Join(" | ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));

    Assert.Multiple(() => {
      Assert.That(match.Handler.Id, Is.EqualTo("fsg"));
      Assert.That(result.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadLocated),
        $"handler={match.Handler.Id}; artifacts={artifacts}; diagnostics={diagnostics}");
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin" || a.Name.StartsWith("payload_candidates/", StringComparison.Ordinal)),
        $"handler={match.Handler.Id}; artifacts={artifacts}; diagnostics={diagnostics}");
    });
  }

  [Test]
  public void WinUpackFirstSamples_UseNamedHandler() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    var samples = Directory.EnumerateFiles(Path.Combine(root!, "WinUpack")).Take(5).ToList();
    Assert.That(samples, Has.Count.EqualTo(5));

    foreach (var sample in samples) {
      var bytes = File.ReadAllBytes(sample);
      var match = ExecutablePackerHandlers.DetectBest(bytes);
      Assert.That(match, Is.Not.Null, Path.GetFileName(sample));

      var packed = match!.Handler.Parse(bytes, match.Detection);
      var result = match.Handler.Unpack(packed, new());
      Assert.Multiple(() => {
        Assert.That(match.Handler.Id, Is.EqualTo("winupack"), Path.GetFileName(sample));
        Assert.That(result.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadLocated), Path.GetFileName(sample));
        Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True, Path.GetFileName(sample));
      });
    }
  }

  [Test]
  public void NsPackFirstSamples_UseNamedHandler() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    var samples = Directory.EnumerateFiles(Path.Combine(root!, "NSPack")).Take(5).ToList();
    Assert.That(samples, Has.Count.EqualTo(5));

    foreach (var sample in samples) {
      var bytes = File.ReadAllBytes(sample);
      var match = ExecutablePackerHandlers.DetectBest(bytes);
      Assert.That(match, Is.Not.Null, Path.GetFileName(sample));

      var packed = match!.Handler.Parse(bytes, match.Detection);
      var result = match.Handler.Unpack(packed, new());
      var artifacts = string.Join(", ", result.Artifacts.Select(a => a.Name));
      Assert.Multiple(() => {
        Assert.That(match.Handler.Id, Is.EqualTo("nspack"), Path.GetFileName(sample));
        Assert.That(result.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadLocated), Path.GetFileName(sample));
        Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True, $"{Path.GetFileName(sample)} artifacts={artifacts}");
      });
    }
  }

  [Test]
  public void YodaCrypterFirstSamples_UseNamedHandler() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    var samples = Directory.EnumerateFiles(Path.Combine(root!, "Yoda-Crypter")).Take(5).ToList();
    Assert.That(samples, Has.Count.EqualTo(5));

    foreach (var sample in samples) {
      var bytes = File.ReadAllBytes(sample);
      var match = ExecutablePackerHandlers.DetectBest(bytes);
      Assert.That(match, Is.Not.Null, Path.GetFileName(sample));

      var packed = match!.Handler.Parse(bytes, match.Detection);
      var result = match.Handler.Unpack(packed, new());
      var artifacts = string.Join(", ", result.Artifacts.Select(a => a.Name));
      Assert.Multiple(() => {
        Assert.That(match.Handler.Id, Is.EqualTo("yodacrypter"), Path.GetFileName(sample));
        Assert.That(result.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadLocated), Path.GetFileName(sample));
        Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True, $"{Path.GetFileName(sample)} artifacts={artifacts}");
      });
    }
  }

  [Test]
  public void ThemidaFirstSamples_UseNamedHandler() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assert.That(root, Is.Not.Null, "Set CWB_DATASET to the dataset 'packed' directory or set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download it.");
    var samples = Directory.EnumerateFiles(Path.Combine(root!, "Themida")).Take(5).ToList();
    Assert.That(samples, Has.Count.EqualTo(5));

    foreach (var sample in samples) {
      var bytes = File.ReadAllBytes(sample);
      var match = ExecutablePackerHandlers.DetectBest(bytes);
      Assert.That(match, Is.Not.Null, Path.GetFileName(sample));

      var packed = match!.Handler.Parse(bytes, match.Detection);
      var result = match.Handler.Unpack(packed, new());
      var artifacts = string.Join(", ", result.Artifacts.Select(a => a.Name));
      Assert.Multiple(() => {
        Assert.That(match.Handler.Id, Is.EqualTo("themida"), Path.GetFileName(sample));
        Assert.That(result.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadLocated), Path.GetFileName(sample));
        Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True, $"{Path.GetFileName(sample)} artifacts={artifacts}");
      });
    }
  }

  private static void Bump<T>(Dictionary<T, int> d, T k, int add = 1) where T : notnull =>
    d[k] = d.TryGetValue(k, out var v) ? v + add : add;

  private static bool HasPayloadArtifact(UnpackResult result) =>
    result.Artifacts.Any(a =>
      a.Name is "compressed_payload.bin" or "decompressed_payload.bin" or "memory_image.bin" ||
      a.Name.StartsWith("payload_candidates/", StringComparison.Ordinal) ||
      a.Name.StartsWith("reconstructed/", StringComparison.Ordinal));

  private static HashSet<string> ParsePackingBoxPackerNames(string manifest) {
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var raw in File.ReadLines(manifest)) {
      if (raw.Length == 0 || char.IsWhiteSpace(raw[0]) || raw[0] == '#')
        continue;
      var line = raw.Split('#', 2)[0].TrimEnd();
      if (!line.EndsWith(":", StringComparison.Ordinal))
        continue;
      var name = line[..^1];
      if (name.Equals("defaults", StringComparison.Ordinal))
        continue;
      names.Add(name);
    }
    return names;
  }

  private static Dictionary<string, string> PackingBoxHandlerAliases() => new(StringComparer.Ordinal) {
    ["Alienyze"] = "alienyze",
    ["Alternate_EXE_Packer"] = "upx",
    ["Amber"] = "amber",
    ["ASPack"] = "aspack",
    ["ASProtect"] = "ASProtect",
    ["BeRo"] = "beroexepacker",
    ["BZEXE"] = "bzexe",
    ["Crinkler"] = "Crinkler",
    ["Eronana_Packer"] = "eronanapacker",
    ["EXE32Pack"] = "exe32pack",
    ["Enigma_Virtual_Box"] = "enigmavirtualbox",
    ["eXPressor"] = "expressor",
    ["FSG"] = "fsg",
    ["GZEXE"] = "gzexe",
    ["GoPacker"] = "gopacker",
    ["Huan"] = "huan",
    ["JDPack"] = "jdpack",
    ["Kkrunchy"] = "Kkrunchy",
    ["LZEXE"] = "LzExe",
    ["MEW"] = "mew",
    ["MoleBox"] = "molebox",
    ["MPRESS"] = "mpress",
    ["NeoLite"] = "neolite",
    ["NSPack"] = "nspack",
    ["Origami"] = "origami",
    ["Papaw"] = "papaw",
    ["PECompact"] = "pecompact",
    ["PE-Toy"] = "petoy",
    ["PEtite"] = "petite",
    ["Packman"] = "packman",
    ["RLPack"] = "rlpack",
    ["PyPePacker"] = "pypepacker",
    ["Silent_Packer"] = "silent_packer",
    ["SimpleDpack"] = "simpledpack",
    ["Squishy"] = "squishy",
    ["Telock"] = "telock",
    ["Themida"] = "themida",
    ["Upack"] = "winupack",
    ["UPX"] = "upx",
    ["VMProtect"] = "VmProtect",
    ["WinUPack"] = "winupack",
    ["Yoda_Crypter"] = "yodacrypter",
    ["Yoda_Protector"] = "yodaprotector",
    ["hXOR-Packer"] = "hxor",
    ["Xor_Packer"] = "xor_packer",
  };

  private static string EscapeJson(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

  private static int ProbeSampleLimit() {
    var value = Environment.GetEnvironmentVariable("CWB_DATASET_LIMIT");
    return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 5;
  }
}
