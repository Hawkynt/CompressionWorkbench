#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

public abstract class MinorExecutablePackerHandlerBase : IExecutablePackerHandler {
  public abstract string Id { get; }
  public abstract string DisplayName { get; }

  public virtual ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  protected abstract bool IsPackerSection(string name);
  protected abstract ReadOnlySpan<byte> LiteralSignature { get; }

  public virtual DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    var hasLiteral = LiteralSignature.Length > 0 && image.IndexOf(LiteralSignature) >= 0;

    var match = hasSection || hasLiteral;
    return match
      ? new(true, this.Id, 0.85, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{this.DisplayName} signature not found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.DisplayName,
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  public virtual UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect;
    var rebuilt = false;

    // 1. Locate packer sections
    var sections = PackerScanner.GetPeSectionRanges(packed.OriginalImage);
    var locatedPayloads = new List<(string Name, byte[] Data)>();
    foreach (var s in sections) {
      if (IsPackerSection(s.Name) && s.RawSize > 0) {
        var len = (int)Math.Min(s.RawSize, (uint)(packed.OriginalImage.Length - s.RawOffset));
        var data = packed.OriginalImage.AsSpan((int)s.RawOffset, len).ToArray();
        locatedPayloads.Add((s.Name, data));
        level = ExecutableUnpackLevel.PayloadLocated;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      }
    }
    AddLocatedPayloadArtifacts(artifacts, locatedPayloads);

    // 2. Try generic aPLib decompress
    if (AplibSectionPackerHandler.TryFindAplibPayload(packed.OriginalImage, options.MaximumDecompressedSize, out var decoded)) {
      artifacts.Add(new("decompressed_payload.bin", decoded, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
      if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info) {
        try {
          var pe = PeRebuilder.RebuildSynthetic(info, decoded);
          artifacts.Add(new("reconstructed/reconstructed.exe", pe, "stored"));
          level = ExecutableUnpackLevel.RebuiltExecutable;
          caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
          rebuilt = true;
        } catch {
          // Ignored
        }
      }
    }
    // 3. Try generic NRV decompress if not rebuilt yet
    else if (!rebuilt && GenericNrvPackedPeHandler.TryFindNrvPayload(packed.OriginalImage, options.MaximumDecompressedSize, out var nrvDecoded)) {
      artifacts.Add(new("decompressed_payload.bin", nrvDecoded, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
      if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info) {
        try {
          var pe = PeRebuilder.RebuildSynthetic(info, nrvDecoded);
          artifacts.Add(new("reconstructed/reconstructed.exe", pe, "stored"));
          level = ExecutableUnpackLevel.RebuiltExecutable;
          caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
        } catch {
          // Ignored
        }
      }
    }

    if (level == ExecutableUnpackLevel.DetectionOnly) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, $"{this.DisplayName} detected but no packed payload section found.", true));
    } else if (level == ExecutableUnpackLevel.PayloadLocated) {
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"{this.DisplayName} payload located, but it could not be decoded as a cleanly-terminated aPLib or NRV stream.", true));
    }

    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64) caps |= ExecutableUnpackCapabilities.SupportsX64;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{this.Id}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void AddLocatedPayloadArtifacts(List<UnpackArtifact> artifacts, IReadOnlyList<(string Name, byte[] Data)> payloads) {
    if (payloads.Count == 0)
      return;
    if (payloads.Count == 1) {
      artifacts.Add(new("compressed_payload.bin", payloads[0].Data, "stored"));
      return;
    }

    for (var i = 0; i < payloads.Count; i++)
      artifacts.Add(new($"payload_candidates/candidate_{i:000}_{Sanitize(payloads[i].Name)}.bin", payloads[i].Data, "stored"));
  }

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "payload" : sb.ToString();
  }
}

public sealed class AlienyzeExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "alienyze";
  public override string DisplayName => "Alienyze";
  protected override bool IsPackerSection(string name) => name.Contains("alien", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Alienyze"u8;
}

public sealed class AmberExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "amber";
  public override string DisplayName => "Amber reflective PE loader";
  protected override bool IsPackerSection(string name) => false;
  protected override ReadOnlySpan<byte> LiteralSignature => "amber"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var hasAscii = image.IndexOf("amber"u8) >= 0 || image.IndexOf("Amber"u8) >= 0 || image.IndexOf("AMBER"u8) >= 0;
    var hasUtf16 = image.IndexOf("A\0m\0b\0e\0r\0"u8) >= 0 || image.IndexOf("a\0m\0b\0e\0r\0"u8) >= 0;
    if (hasAscii || hasUtf16)
      return new(true, this.Id, 0.90, []);
    return new(false, this.Id, 0, []);
  }

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    if (generic.Level >= ExecutableUnpackLevel.PayloadDecompressed)
      return generic;

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildAmberMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var payload = LocateReflectivePayloadSection(packed.OriginalImage);
    var level = ExecutableUnpackLevel.DetectionOnly;
    if (payload is { } p) {
      artifacts.Add(new("compressed_payload.bin", p.Data, "amber-section"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"Amber reflective-loader payload section '{p.Name}' was located. Managed reflective-loader transform reversal is not implemented.",
        true));
    } else {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        "Amber was detected, but no large non-standard payload section could be located.", true));
    }

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct AmberPayload(string Name, byte[] Data);

  private static AmberPayload? LocateReflectivePayloadSection(byte[] image) {
    var sections = PackerScanner.GetPeSectionRanges(image);
    var candidate = sections
      .Where(s => s.RawSize >= 4096 && s.RawOffset > 0 && s.RawOffset < image.Length && !IsCommonPeSection(s.Name))
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (candidate.RawSize == 0)
      return null;
    var len = (int)Math.Min(candidate.RawSize, (uint)(image.Length - candidate.RawOffset));
    return new(candidate.Name, image.AsSpan((int)candidate.RawOffset, len).ToArray());
  }

  private static bool IsCommonPeSection(string name) =>
    name is ".text" or ".data" or ".rdata" or ".bss" or ".idata" or ".CRT" or ".tls" or ".rsrc" or ".reloc";

  private byte[] BuildAmberMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"amber\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"loaderType\": \"reflective-pe-loader\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

public sealed class BeRoExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "beroexepacker";
  public override string DisplayName => "BeRoEXEPacker";
  protected override bool IsPackerSection(string name) =>
    name.Contains("bero", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("gu_idata", StringComparison.Ordinal) ||
    name.Equals("gu_rsrc", StringComparison.Ordinal);
  protected override ReadOnlySpan<byte> LiteralSignature => "BeRo"u8;
}

public sealed class EronanaExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "eronanapacker";
  public override string DisplayName => "Eronana Packer";
  protected override bool IsPackerSection(string name) => name.Contains("eron", StringComparison.OrdinalIgnoreCase) || name.Equals(".packer", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Eronana"u8;
}

public sealed class Exe32packExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "exe32pack";
  public override string DisplayName => "Exe32pack";
  protected override bool IsPackerSection(string name) =>
    name is ".i" or ".f" or ".c" or ".v" or ".h";
  protected override ReadOnlySpan<byte> LiteralSignature => "exe32pack"u8;
  
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasExe32 = image.IndexOf("exe32pack"u8) >= 0;
    var hasKnownSection = sections.Any(s => s.Name == ".i" || s.Name == ".f" || s.Name == ".c" || s.Name == ".v" || s.Name == ".h");
    if (hasExe32 || hasKnownSection) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

public sealed class ExpressorExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "expressor";
  public override string DisplayName => "EXpressor";
  protected override bool IsPackerSection(string name) =>
    name.Contains("exp", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("ex_", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "EXpressor"u8;
}

public sealed class JdpackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "jdpack";
  public override string DisplayName => "JDPack";
  protected override bool IsPackerSection(string name) => name.Contains("jd", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "JDPack"u8;
}

public sealed class MoleboxExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "molebox";
  public override string DisplayName => "Molebox";
  protected override bool IsPackerSection(string name) =>
    name.Contains("mole", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("mbx", StringComparison.OrdinalIgnoreCase) ||
    int.TryParse(name, out _);
  protected override ReadOnlySpan<byte> LiteralSignature => "Molebox"u8;
}

public sealed class NeoliteExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "neolite";
  public override string DisplayName => "Neolite";
  protected override bool IsPackerSection(string name) => name.Contains("neolit", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "NeoLite"u8;
}

public sealed class NsPackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "nspack";
  public override string DisplayName => "NSPack";
  protected override bool IsPackerSection(string name) =>
    name.StartsWith("nsp", StringComparison.OrdinalIgnoreCase) ||
    name.StartsWith(".nsp", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "NsPack"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    var hasLiteral = image.IndexOf(LiteralSignature) >= 0;
    return hasSection || hasLiteral
      ? new(true, this.Id, hasSection ? 0.93 : 0.88, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "NSPack marker was not found.", true)]);
  }

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    var payload = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => IsPackerSection(s.Name) && s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.Name.Equals("nsp1", StringComparison.OrdinalIgnoreCase) || s.Name.Equals(".nsp1", StringComparison.OrdinalIgnoreCase))
      .ThenByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (payload.RawSize == 0)
      return generic;

    if (generic.Level >= ExecutableUnpackLevel.PayloadDecompressed && generic.Artifacts.Any(a => a.Name == "compressed_payload.bin"))
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json" && a.Name != "compressed_payload.bin" && !a.Name.StartsWith("payload_candidates/", StringComparison.Ordinal))
      .ToList();
    var len = (int)Math.Min(payload.RawSize, (uint)(packed.OriginalImage.Length - payload.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)payload.RawOffset, len).ToArray(), "nspack-section"));

    if (generic.Level >= ExecutableUnpackLevel.PayloadDecompressed) {
      var passthrough = generic with { Artifacts = artifacts };
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, passthrough), "stored"));
      return passthrough with { Artifacts = artifacts };
    }

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"NSPack payload section '{payload.Name}' was located. Managed NSPack decompression and transform reversal are not implemented.",
        true),
    };
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}

public sealed class MewExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "mew";
  public override string DisplayName => "MEW";
  protected override bool IsPackerSection(string name) =>
    name.StartsWith("MEW", StringComparison.OrdinalIgnoreCase) ||
    name.StartsWith(".MEW", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => [];

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasMewSection = sections.Any(s => IsPackerSection(s.Name));
    return hasMewSection
      ? new(true, this.Id, 0.92, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "MEW section marker was not found.", true)]);
  }

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    if (generic.Level >= ExecutableUnpackLevel.PayloadLocated)
      return generic;

    var section = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (section.RawSize == 0)
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json")
      .ToList();
    var len = (int)Math.Min(section.RawSize, (uint)(packed.OriginalImage.Length - section.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)section.RawOffset, len).ToArray(), "mew-section"));

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "MEW packed section was located, but managed MEW transform/decompression recovery is not implemented yet.",
        true),
    };
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}

public sealed class PetiteExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "petite";
  public override string DisplayName => "PEtite";
  protected override bool IsPackerSection(string name) => name.StartsWith(".petite", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Petite"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasPetiteSection = sections.Any(s => IsPackerSection(s.Name));
    var hasMarker = image.IndexOf("Petite"u8) >= 0;
    return hasPetiteSection || hasMarker
      ? new(true, this.Id, hasPetiteSection ? 0.93 : 0.82, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PEtite marker was not found.", true)]);
  }
}

public sealed class YodaProtectorExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "yodaprotector";
  public override string DisplayName => "Yoda's Protector";
  protected override bool IsPackerSection(string name) => name.Contains("yP", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "yoda"u8;
}

public sealed class YodaCrypterExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "yodacrypter";
  public override string DisplayName => "Yoda's Crypter";
  protected override bool IsPackerSection(string name) =>
    name.Equals("yC", StringComparison.Ordinal) ||
    name.Equals(".yC", StringComparison.Ordinal);
  protected override ReadOnlySpan<byte> LiteralSignature => "Yoda's"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    var hasLiteral = image.IndexOf(LiteralSignature) >= 0;
    return hasSection || hasLiteral
      ? new(true, this.Id, hasSection ? 0.93 : 0.82, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Yoda's Crypter marker was not found.", true)]);
  }

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    if (generic.Level >= ExecutableUnpackLevel.PayloadDecompressed)
      return generic;

    var payload = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => IsPackerSection(s.Name) && s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (payload.RawSize == 0)
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json" && a.Name != "compressed_payload.bin" && !a.Name.StartsWith("payload_candidates/", StringComparison.Ordinal))
      .ToList();
    var len = (int)Math.Min(payload.RawSize, (uint)(packed.OriginalImage.Length - payload.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)payload.RawOffset, len).ToArray(), "yodacrypter-section"));

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"Yoda's Crypter section '{payload.Name}' was located. Managed decryption and import/entrypoint restoration are not implemented.",
        true),
    };
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}

public sealed class HxorPackerExecutablePackerHandler : IExecutablePackerHandler {
  private const int PayloadRecordSize = 0x114;
  private static ReadOnlySpan<byte> PayloadRecordMagic => "FIFA"u8;

  public string Id => "hxor";
  public string DisplayName => "hXOR-Packer";

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var hasStubMarker = image.IndexOf("hXOR Packer"u8) >= 0 || image.IndexOf("hXOR"u8) >= 0;
    var payload = LocatePayload(image);
    var match = hasStubMarker && payload != null;
    return match
      ? new(true, this.Id, 0.9, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "hXOR stub marker and payload record were not found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.DisplayName,
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var payload = LocatePayload(packed.OriginalImage);
    if (payload == null) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        "hXOR was detected, but its appended payload record could not be located.", true));
      var detectionOnly = new UnpackResult(ExecutableUnpackLevel.DetectionOnly, caps, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, detectionOnly), "stored"));
      return detectionOnly with { Artifacts = artifacts };
    }

    artifacts.Add(new("packer_metadata/hxor_payload_record.bin", payload.Value.Record, "stored"));
    artifacts.Add(new("compressed_payload.bin", payload.Value.Payload, "hxor-transformed"));
    diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
      "hXOR transformed payload was located. Managed Huffman/XOR transform reversal is not implemented yet.",
      true));
    caps |= ExecutableUnpackCapabilities.CanLocatePayload;

    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct HxorPayload(byte[] Record, byte[] Payload);

  private static HxorPayload? LocatePayload(ReadOnlySpan<byte> image) {
    var marker = image.LastIndexOf(PayloadRecordMagic);
    if (marker < 0 || marker + PayloadRecordSize >= image.Length)
      return null;

    var fileName = PackerScanner.ReadAsciiAt(image, marker + PayloadRecordMagic.Length, 252);
    if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
      return null;

    return new(
      image.Slice(marker, PayloadRecordSize).ToArray(),
      image[(marker + PayloadRecordSize)..].ToArray());
  }

  private byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"hxor\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"payloadRecord\": \"FIFA\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

public sealed class SimpleDpackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "simpledpack";
  public override string DisplayName => "SimpleDpack";
  protected override bool IsPackerSection(string name) => name.Equals(".dpack", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "SimpleDpack"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasDpackSection = sections.Any(s => s.Name.Equals(".dpack", StringComparison.OrdinalIgnoreCase));
    var hasMarker = image.IndexOf("SimpleDpack"u8) >= 0 || image.IndexOf("simpledpack"u8) >= 0;
    return hasDpackSection
      ? new(true, this.Id, hasMarker ? 0.95 : 0.85, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "SimpleDpack .dpack section was not found.", true)]);
  }
}

public sealed class ThemidaExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "themida";
  public override string DisplayName => "Themida";
  protected override bool IsPackerSection(string name) =>
    name.Contains("themida", StringComparison.OrdinalIgnoreCase) ||
    name.Equals(".boot", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => [] ;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasThemidaSection = sections.Any(s => s.Name.Contains("themida", StringComparison.OrdinalIgnoreCase));
    var hasBootSection = sections.Any(s => s.Name.Equals(".boot", StringComparison.OrdinalIgnoreCase));
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    if (hasThemidaSection || (hasBootSection && emptyCount >= 2))
      return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

public sealed class TelockExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "telock";
  public override string DisplayName => "TELock";
  protected override bool IsPackerSection(string name) =>
    string.IsNullOrWhiteSpace(name) ||
    name.Contains("tElock", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "tElock"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasLiteral = image.IndexOf("tElock"u8) >= 0 || image.IndexOf("TELock"u8) >= 0;
    var hasSection = sections.Any(s => s.Name.Contains("tElock", StringComparison.OrdinalIgnoreCase));
    if (hasLiteral || hasSection) return new(true, this.Id, 0.85, []);
    if (HasTelockBlankEntrySection(image))
      return new(true, this.Id, 0.55, []);
    return new(false, this.Id, 0, []);
  }

  private static bool HasTelockBlankEntrySection(ReadOnlySpan<byte> image) {
    if (image.Length < 0x40)
      return false;
    var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (peOffset < 0 || peOffset + 24 > image.Length)
      return false;
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
    if (sectionCount < 3)
      return false;
    var optionalOffset = peOffset + 24;
    if (optionalOffset + 20 > image.Length)
      return false;
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image[(optionalOffset + 16)..]);
    var sectionOffset = optionalOffset + optionalSize;
    var lastOffset = sectionOffset + (sectionCount - 1) * 40;
    if (lastOffset + 40 > image.Length)
      return false;

    var lastName = Encoding.ASCII.GetString(image.Slice(lastOffset, 8)).TrimEnd('\0');
    var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(lastOffset + 8)..]);
    var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image[(lastOffset + 12)..]);
    var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(lastOffset + 16)..]);
    if (!string.IsNullOrWhiteSpace(lastName) || rawSize == 0)
      return false;

    return entry >= virtualAddress && entry < virtualAddress + Math.Max(virtualSize, rawSize);
  }
}

public sealed class WinUpackFallbackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "winupackfallback";
  public override string DisplayName => "WinUpack";
  protected override bool IsPackerSection(string name) => name.Contains("Upack", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Upack"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasLiteral = image.IndexOf("Upack"u8) >= 0 || image.IndexOf("By Dwing"u8) >= 0;
    var hasSection = sections.Any(s => s.Name.Contains("Upack", StringComparison.OrdinalIgnoreCase));
    var hasPs = sections.Count > 0 && sections[0].Name.StartsWith("PS");
    if (hasLiteral || hasSection || hasPs) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

public sealed class FsgFallbackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "fsgfallback";
  public override string DisplayName => "FSG";
  protected override bool IsPackerSection(string name) =>
    name.Equals("ta", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("a", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("fsg", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "FSG!"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    var baseMatch = image.IndexOf(LiteralSignature) >= 0;
    if (baseMatch || emptyCount >= 2) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    if (generic.Level >= ExecutableUnpackLevel.PayloadLocated)
      return generic;

    var section = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (section.RawSize == 0)
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json")
      .ToList();
    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "FSG payload section was located by structural fallback, but the managed FSG transform/decompression path did not decode it.",
        true),
    };
    var len = (int)Math.Min(section.RawSize, (uint)(packed.OriginalImage.Length - section.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)section.RawOffset, len).ToArray(), "fsg-section"));

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}
