#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ps1MemoryCard;

/// <summary>
/// Sony PlayStation memory-card filesystem. One hardware-visible card bank is
/// always the canonical 128 KiB layout (one metadata block plus fifteen 8 KiB
/// save blocks). Larger third-party cards from the PS1 era are represented as
/// bank-switched collections of independent canonical banks; no enlarged
/// fictional allocation table is invented.
/// </summary>
public sealed class Ps1MemoryCardFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable,
  IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema {

  private static readonly int[] CanonicalBankCounts = [1, 2, 4, 8, 16, 32, 64];

  public string Id => "Ps1MemoryCard";
  public string DisplayName => "PlayStation Memory Card";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mcr";
  public IReadOnlyList<string> Extensions => [".mcr", ".mcd", ".mem", ".psm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'M', (byte)'C'], Confidence: 0.45)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "8 KiB save blocks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sony PlayStation 128 KiB memory card and bank-switched multi-card image";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("Banks", "Card banks", FormatOptionKind.Enum, "Auto",
      ["Auto", "1", "2", "4", "8", "16", "32", "64"],
      "Number of independent 128 KiB PS1 card banks. Auto chooses the smallest historical power-of-two bank count that fits."),
  ];

  public IReadOnlyList<long> CanonicalSizes
    => CanonicalBankCounts.Select(b => (long)b * Ps1MemoryCardImage.CardSize).ToArray();

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var parsed = Ps1MemoryCardImage.Read(stream);
    return parsed.Saves.Select((save, index) => new ArchiveEntryInfo(
      index,
      Ps1MemoryCardImage.ArchiveName(save, parsed.BankCount),
      save.DeclaredSize,
      (long)save.Blocks.Count * Ps1MemoryCardImage.BlockSize,
      "Stored blocks",
      false,
      false,
      null,
      Kind: $"bank:{save.BankIndex + 1}"))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var parsed = Ps1MemoryCardImage.Read(stream);
    foreach (var save in parsed.Saves) {
      var name = Ps1MemoryCardImage.ArchiveName(save, parsed.BankCount);
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, save.Data);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var parsed = Ps1MemoryCardImage.Read(archive);
    var save = ResolveSave(parsed, entryName, allowMissing: false)
      ?? throw new FileNotFoundException($"PS1 memory-card save '{entryName}' was not found.");
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(save.Data, writable: false), save.Data.Length, leaveOpen: false);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var requestedBanks = ParseRequestedBanks(options.GetOption("Banks", "Auto"));
    var saves = PlanFreshSaves(inputs, requestedBanks, out var bankCount);
    var image = Ps1MemoryCardImage.Build(bankCount, saves);
    output.Position = 0;
    output.SetLength(0);
    output.Write(image);
    output.Flush();
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var parsed = Ps1MemoryCardImage.Read(archive);
    var saves = parsed.Saves.Select(CloneForBuild).ToList();

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var data = input.ReadContent();
      var declared = Ps1MemoryCardImage.RoundStoredSize(data.Length);
      var (explicitBank, rawName) = Ps1MemoryCardImage.ParseArchiveName(input.ArchiveName);
      var bank = ResolveTargetBankForAdd(saves, parsed.BankCount, explicitBank, rawName, declared);

      saves.RemoveAll(s => s.BankIndex == bank && string.Equals(s.Name, rawName, StringComparison.Ordinal));
      EnsureBankHasCapacity(saves, bank, declared);
      saves.Add(new Ps1MemoryCardImage.Save(bank, rawName, declared, data, []));
    }

    Ps1MemoryCardImage.Rewrite(archive, Ps1MemoryCardImage.Build(parsed.BankCount, saves));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    entryNames ??= [];
    var parsed = Ps1MemoryCardImage.Read(archive);
    var targets = new HashSet<Ps1MemoryCardImage.Save>();
    foreach (var entryName in entryNames) {
      var save = ResolveSave(parsed, entryName, allowMissing: true);
      if (save != null) targets.Add(save);
    }
    if (targets.Count == 0) return;

    var image = parsed.Bytes;
    foreach (var save in targets)
      MarkDeleted(image, save);
    Ps1MemoryCardImage.Rewrite(archive, image);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("PS1 memory cards currently support consolidate-at-start defragmentation only.");

    options.CancellationToken.ThrowIfCancellationRequested();
    var parsed = Ps1MemoryCardImage.Read(archive);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, this.EnumerateExtentsFromParsed(parsed),
      $"Scanning {parsed.BankCount} PS1 memory-card bank(s)"));

    var rebuilt = Ps1MemoryCardImage.Build(parsed.BankCount, parsed.Saves.Select(CloneForBuild));
    options.CancellationToken.ThrowIfCancellationRequested();
    VerifyLiveIdentity(parsed, rebuilt);
    Ps1MemoryCardImage.Rewrite(archive, rebuilt);

    var after = Ps1MemoryCardImage.Read(archive);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, this.EnumerateExtentsFromParsed(after),
      "Each bank compacted; bank count and bank identity preserved"));
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var parsed = Ps1MemoryCardImage.Read(input);
    var requiredBanks = parsed.Saves.Count == 0 ? 1 : parsed.Saves.Max(s => s.BankIndex) + 1;
    var targetBanks = CanonicalBankCounts.FirstOrDefault(b => b >= requiredBanks && b <= parsed.BankCount);
    if (targetBanks == 0) targetBanks = parsed.BankCount;

    var rebuilt = Ps1MemoryCardImage.Build(targetBanks, parsed.Saves.Select(CloneForBuild));
    VerifyLiveIdentity(parsed, rebuilt);
    output.Position = 0;
    output.SetLength(0);
    output.Write(rebuilt);
    output.Flush();
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => Ps1MemoryCardImage.Wipe(image, wipeClusterTips, wipeDeletedEntries);

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => this.EnumerateExtentsFromParsed(Ps1MemoryCardImage.Read(image));

  private IReadOnlyList<DefragBlockInfo> EnumerateExtentsFromParsed(Ps1MemoryCardImage.Parsed parsed) {
    var result = new List<DefragBlockInfo>();
    foreach (var extent in Ps1MemoryCardImage.EnumeratePhysicalExtents(parsed))
      result.Add(new DefragBlockInfo(
        extent.Offset,
        extent.Length,
        extent.Name?.EndsWith("metadata", StringComparison.Ordinal) == true
          ? DefragBlockKind.MetadataReserved
          : extent.Used ? DefragBlockKind.Used : DefragBlockKind.Free,
        extent.Name));
    return result;
  }

  private static int ParseRequestedBanks(string value) {
    if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value))
      return 0;
    if (!int.TryParse(value, out var banks) || !CanonicalBankCounts.Contains(banks))
      throw new ArgumentException("PS1 card Banks must be Auto, 1, 2, 4, 8, 16, 32, or 64.", nameof(value));
    return banks;
  }

  private static List<Ps1MemoryCardImage.Save> PlanFreshSaves(
      IReadOnlyList<ArchiveInputInfo> inputs, int requestedBanks, out int bankCount) {
    var explicitSaves = new List<Ps1MemoryCardImage.Save>();
    var automatic = new List<(string Name, int Declared, byte[] Data)>();

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var data = input.ReadContent();
      var declared = Ps1MemoryCardImage.RoundStoredSize(data.Length);
      var (explicitBank, rawName) = Ps1MemoryCardImage.ParseArchiveName(input.ArchiveName);
      if (explicitBank is { } bank) {
        if (bank >= Ps1MemoryCardImage.MaxBanks)
          throw new InvalidDataException($"PS1 bank {bank + 1} exceeds the supported {Ps1MemoryCardImage.MaxBanks}-bank limit.");
        explicitSaves.Add(new Ps1MemoryCardImage.Save(bank, rawName, declared, data, []));
      } else {
        automatic.Add((rawName, declared, data));
      }
    }

    var duplicateAutomatic = automatic.GroupBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
    if (duplicateAutomatic != null)
      throw new InvalidDataException(
        $"Duplicate unqualified PS1 save name '{duplicateAutomatic.Key}'. Use bankNN/name to place same-named saves in different banks.");

    var saves = new List<Ps1MemoryCardImage.Save>(explicitSaves);
    ValidatePerBank(saves);

    foreach (var item in automatic) {
      var bank = Enumerable.Range(0, Ps1MemoryCardImage.MaxBanks).FirstOrDefault(candidate =>
        !saves.Any(s => s.BankIndex == candidate && string.Equals(s.Name, item.Name, StringComparison.Ordinal))
        && UsedBlocks(saves, candidate) + Ps1MemoryCardImage.BlocksRequired(item.Declared) <= Ps1MemoryCardImage.DataBlocksPerBank);
      if (UsedBlocks(saves, bank) + Ps1MemoryCardImage.BlocksRequired(item.Declared) > Ps1MemoryCardImage.DataBlocksPerBank)
        throw new InvalidDataException("PS1 memory-card inputs need more than 64 banks.");
      saves.Add(new Ps1MemoryCardImage.Save(bank, item.Name, item.Declared, item.Data, []));
    }

    var requiredBanks = saves.Count == 0 ? 1 : saves.Max(s => s.BankIndex) + 1;
    bankCount = requestedBanks != 0
      ? requestedBanks
      : CanonicalBankCounts.FirstOrDefault(b => b >= requiredBanks);
    if (bankCount == 0)
      throw new InvalidDataException("PS1 memory-card inputs need more than 64 banks.");
    if (requiredBanks > bankCount)
      throw new InvalidDataException($"Inputs require {requiredBanks} bank(s), but creation was pinned to {bankCount}.");
    ValidatePerBank(saves);
    return saves;
  }

  private static int ResolveTargetBankForAdd(
      IReadOnlyList<Ps1MemoryCardImage.Save> saves,
      int bankCount,
      int? explicitBank,
      string rawName,
      int declaredSize) {
    if (explicitBank is { } bank) {
      if (bank < 0 || bank >= bankCount)
        throw new InvalidDataException($"PS1 bank {bank + 1} is outside this {bankCount}-bank image.");
      return bank;
    }

    if (bankCount == 1) return 0;
    var matches = saves.Where(s => string.Equals(s.Name, rawName, StringComparison.Ordinal)).ToArray();
    if (matches.Length == 1) return matches[0].BankIndex;
    if (matches.Length > 1)
      throw new InvalidDataException(
        $"Save name '{rawName}' exists in multiple PS1 banks; use bankNN/{rawName} to disambiguate replacement.");

    var need = Ps1MemoryCardImage.BlocksRequired(declaredSize);
    for (var candidate = 0; candidate < bankCount; ++candidate)
      if (UsedBlocks(saves, candidate) + need <= Ps1MemoryCardImage.DataBlocksPerBank)
        return candidate;
    throw new InvalidDataException($"No PS1 memory-card bank has {need} free block(s) for '{rawName}'.");
  }

  private static Ps1MemoryCardImage.Save? ResolveSave(
      Ps1MemoryCardImage.Parsed parsed, string archiveName, bool allowMissing) {
    var (explicitBank, rawName) = Ps1MemoryCardImage.ParseArchiveName(archiveName);
    var matches = parsed.Saves.Where(s =>
      (explicitBank == null || s.BankIndex == explicitBank.Value)
      && string.Equals(s.Name, rawName, StringComparison.Ordinal)).ToArray();

    if (matches.Length == 1) return matches[0];
    if (matches.Length > 1)
      throw new InvalidDataException(
        $"Save name '{rawName}' exists in multiple PS1 banks; use bankNN/{rawName} to disambiguate it.");
    if (allowMissing) return null;
    throw new FileNotFoundException($"PS1 memory-card save '{archiveName}' was not found.");
  }

  private static void MarkDeleted(byte[] image, Ps1MemoryCardImage.Save save) {
    foreach (var slot in save.Blocks) {
      var offset = checked(save.BankIndex * Ps1MemoryCardImage.CardSize + (slot + 1) * Ps1MemoryCardImage.FrameSize);
      var frame = image.AsSpan(offset, Ps1MemoryCardImage.FrameSize);
      var state = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame);
      var deletedState = state switch {
        Ps1MemoryCardImage.LiveFirst => Ps1MemoryCardImage.FreeDeletedFirst,
        Ps1MemoryCardImage.LiveMiddle => Ps1MemoryCardImage.FreeDeletedMiddle,
        Ps1MemoryCardImage.LiveLast => Ps1MemoryCardImage.FreeDeletedLast,
        _ => throw new InvalidDataException($"Cannot delete PS1 save '{save.Name}': block state 0x{state:X8} is not live."),
      };
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, deletedState);
      Ps1MemoryCardImage.StampChecksum(frame);
    }
  }

  private static void EnsureBankHasCapacity(
      IReadOnlyList<Ps1MemoryCardImage.Save> saves, int bank, int declaredSize) {
    var used = UsedBlocks(saves, bank);
    var need = Ps1MemoryCardImage.BlocksRequired(declaredSize);
    if (used + need > Ps1MemoryCardImage.DataBlocksPerBank)
      throw new InvalidDataException(
        $"PS1 bank {bank + 1} has {Ps1MemoryCardImage.DataBlocksPerBank - used} free block(s), but {need} are required.");
  }

  private static void ValidatePerBank(IReadOnlyList<Ps1MemoryCardImage.Save> saves) {
    foreach (var bank in saves.GroupBy(s => s.BankIndex)) {
      if (bank.Key < 0 || bank.Key >= Ps1MemoryCardImage.MaxBanks)
        throw new InvalidDataException($"PS1 bank index {bank.Key} is outside the supported range.");
      var duplicate = bank.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
      if (duplicate != null)
        throw new InvalidDataException($"Duplicate PS1 save name '{duplicate.Key}' in bank {bank.Key + 1}.");
      var blocks = bank.Sum(s => Ps1MemoryCardImage.BlocksRequired(s.DeclaredSize));
      if (blocks > Ps1MemoryCardImage.DataBlocksPerBank)
        throw new InvalidDataException($"PS1 bank {bank.Key + 1} needs {blocks} blocks; only 15 exist.");
    }
  }

  private static int UsedBlocks(IEnumerable<Ps1MemoryCardImage.Save> saves, int bank)
    => saves.Where(s => s.BankIndex == bank).Sum(s => Ps1MemoryCardImage.BlocksRequired(s.DeclaredSize));

  private static Ps1MemoryCardImage.Save CloneForBuild(Ps1MemoryCardImage.Save save)
    => new(save.BankIndex, save.Name, save.DeclaredSize, save.Data, []);

  private static void VerifyLiveIdentity(Ps1MemoryCardImage.Parsed before, byte[] rebuilt) {
    using var stream = new MemoryStream(rebuilt, writable: false);
    var after = Ps1MemoryCardImage.Read(stream);
    var expected = before.Saves
      .OrderBy(s => s.BankIndex).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
    var actual = after.Saves
      .OrderBy(s => s.BankIndex).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
    if (expected.Length != actual.Length)
      throw new InvalidOperationException("PS1 memory-card rebuild changed the live save count.");
    for (var i = 0; i < expected.Length; ++i) {
      if (expected[i].BankIndex != actual[i].BankIndex
          || !string.Equals(expected[i].Name, actual[i].Name, StringComparison.Ordinal)
          || expected[i].DeclaredSize != actual[i].DeclaredSize
          || !expected[i].Data.AsSpan().SequenceEqual(actual[i].Data))
        throw new InvalidOperationException(
          $"PS1 memory-card rebuild changed live save '{expected[i].Name}' in bank {expected[i].BankIndex + 1}.");
    }
  }
}
