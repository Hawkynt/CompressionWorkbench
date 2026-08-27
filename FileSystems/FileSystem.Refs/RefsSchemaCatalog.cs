#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal sealed record RefsSchemaDefinition(
  uint SchemaId,
  uint DefinitionSize,
  uint KeyDescriptorSize,
  uint ValueDescriptorSize,
  uint KeyRulesSelector,
  bool SystemSchema,
  byte[] RawValue);

/// <summary>
/// Decodes the self-describing ReFS Schema Table. B+ mutation must select the
/// table's real key comparator from this catalog; bytewise comparison is not a
/// valid generic substitute because the on-disk selector dispatches several
/// different key-rule families.
/// </summary>
internal sealed class RefsSchemaCatalog {
  private const int DefinitionSize = 0x50;
  private const int KeyDescriptorSize = 0x18;
  private const int ValueDescriptorSize = 0x38;
  private const uint MaximumKnownRulesSelectorExclusive = 0x15;

  private readonly Dictionary<uint, RefsSchemaDefinition> _definitions = [];

  public RefsSchemaCatalog(RefsMetadataReader metadata) {
    ArgumentNullException.ThrowIfNull(metadata);

    var primary = ReadRoot(metadata, 3);
    var duplicate = ReadRoot(metadata, 9);
    var selected = primary.Count > 0 ? primary : duplicate;
    if (selected.Count == 0)
      throw new InvalidDataException("Neither ReFS Schema Table copy contains valid definitions.");

    // When both failover copies are readable, a disagreement is not something a
    // writer may silently resolve. Mount-time recovery can select a good copy,
    // but mutation must first know which schema semantics it is preserving.
    if (primary.Count > 0 && duplicate.Count > 0) {
      foreach (var (id, definition) in primary) {
        if (!duplicate.TryGetValue(id, out var other)) continue;
        if (definition.KeyRulesSelector != other.KeyRulesSelector
            || !definition.RawValue.AsSpan().SequenceEqual(other.RawValue))
          throw new InvalidDataException(
            $"ReFS Schema Table failover copies disagree for schema 0x{id:X}.");
      }
    }

    foreach (var item in selected) this._definitions[item.Key] = item.Value;
  }

  public IReadOnlyDictionary<uint, RefsSchemaDefinition> Definitions => this._definitions;

  public bool TryGet(uint schemaId, out RefsSchemaDefinition definition)
    => this._definitions.TryGetValue(schemaId, out definition!);

  public RefsSchemaDefinition Get(uint schemaId)
    => this.TryGet(schemaId, out var definition)
      ? definition
      : throw new NotSupportedException($"ReFS schema 0x{schemaId:X} is not present in the active volume schema catalog.");

  private static Dictionary<uint, RefsSchemaDefinition> ReadRoot(RefsMetadataReader metadata, int rootIndex) {
    var result = new Dictionary<uint, RefsSchemaDefinition>();
    try {
      foreach (var row in metadata.WalkRoot(rootIndex)) {
        if (row.Key.Length != 8 || row.Value.Length < DefinitionSize) continue;
        var schemaId = BinaryPrimitives.ReadUInt32LittleEndian(row.Key.AsSpan(0, 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(row.Key.AsSpan(4, 4)) != 0) continue;

        var definitionSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x00, 4));
        var keyDescriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x04, 4));
        var valueDescriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x18, 4));
        var keyRules = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x1C, 4));
        var selfSchemaId = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x24, 4));
        var systemFlag = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x48, 4));

        if (definitionSize != DefinitionSize
            || keyDescriptorSize != KeyDescriptorSize
            || valueDescriptorSize != ValueDescriptorSize
            || selfSchemaId != schemaId
            || keyRules == 0
            || keyRules >= MaximumKnownRulesSelectorExclusive)
          continue;

        var raw = row.Value.AsSpan(0, DefinitionSize).ToArray();
        result[schemaId] = new RefsSchemaDefinition(
          schemaId,
          definitionSize,
          keyDescriptorSize,
          valueDescriptorSize,
          keyRules,
          systemFlag == 1,
          raw);
      }
    } catch (InvalidDataException) {
      result.Clear();
    }
    return result;
  }
}
