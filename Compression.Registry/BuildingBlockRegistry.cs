namespace Compression.Registry;

/// <summary>
/// Central registry for compression building blocks (algorithm primitives).
/// Populated at startup via source-generated code, similar to <see cref="FormatRegistry"/>.
/// </summary>
public static class BuildingBlockRegistry {

  private static readonly List<IBuildingBlock> _all = [];
  private static readonly Dictionary<string, IBuildingBlock> _byId = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Register a building block.</summary>
  public static void Register(IBuildingBlock block) {
    _all.Add(block);
    _byId[block.Id] = block;
  }

  /// <summary>All registered building blocks.</summary>
  public static IReadOnlyList<IBuildingBlock> All => _all;

  /// <summary>Look up a building block by its unique ID.</summary>
  public static IBuildingBlock? GetById(string id)
    => _byId.GetValueOrDefault(id);

  /// <summary>Reset the registry (for testing only).</summary>
  internal static void Reset() {
    _all.Clear();
    _byId.Clear();
  }
}
