#pragma warning disable CS1591
namespace FileFormat.Zarr;

/// <summary>
/// Represents a zarr constants.
/// </summary>
public static class ZarrConstants {

  /// <summary>
  /// Defines the zarr format key constant value.
  /// </summary>
  public const string ZarrFormatKey = "zarr_format";
  /// <summary>
  /// Defines the shape key constant value.
  /// </summary>
  public const string ShapeKey = "shape";
  /// <summary>
  /// Defines the chunks key constant value.
  /// </summary>
  public const string ChunksKey = "chunks";
  /// <summary>
  /// Defines the dtype key constant value.
  /// </summary>
  public const string DtypeKey = "dtype";
  /// <summary>
  /// Defines the compressor key constant value.
  /// </summary>
  public const string CompressorKey = "compressor";
  /// <summary>
  /// Defines the fill value key constant value.
  /// </summary>
  public const string FillValueKey = "fill_value";
  /// <summary>
  /// Defines the order key constant value.
  /// </summary>
  public const string OrderKey = "order";
  /// <summary>
  /// Defines the filters key constant value.
  /// </summary>
  public const string FiltersKey = "filters";
  /// <summary>
  /// Defines the dimension separator key constant value.
  /// </summary>
  public const string DimensionSeparatorKey = "dimension_separator";

  /// <summary>
  /// Defines the node type key constant value.
  /// </summary>
  public const string NodeTypeKey = "node_type";
  /// <summary>
  /// Defines the data type key constant value.
  /// </summary>
  public const string DataTypeKey = "data_type";
  /// <summary>
  /// Defines the chunk grid key constant value.
  /// </summary>
  public const string ChunkGridKey = "chunk_grid";
  /// <summary>
  /// Defines the chunk key encoding key constant value.
  /// </summary>
  public const string ChunkKeyEncodingKey = "chunk_key_encoding";
  /// <summary>
  /// Defines the codecs key constant value.
  /// </summary>
  public const string CodecsKey = "codecs";
  /// <summary>
  /// Defines the attributes key constant value.
  /// </summary>
  public const string AttributesKey = "attributes";
  /// <summary>
  /// Defines the dimension names key constant value.
  /// </summary>
  public const string DimensionNamesKey = "dimension_names";

  /// <summary>
  /// Defines the id key constant value.
  /// </summary>
  public const string IdKey = "id";
  /// <summary>
  /// Defines the name key constant value.
  /// </summary>
  public const string NameKey = "name";
  /// <summary>
  /// Defines the configuration key constant value.
  /// </summary>
  public const string ConfigurationKey = "configuration";
  /// <summary>
  /// Defines the chunk shape key constant value.
  /// </summary>
  public const string ChunkShapeKey = "chunk_shape";

  /// <summary>
  /// Defines the node type array constant value.
  /// </summary>
  public const string NodeTypeArray = "array";
  /// <summary>
  /// Defines the node type group constant value.
  /// </summary>
  public const string NodeTypeGroup = "group";

  /// <summary>
  /// Provides the v 2 required keys value.
  /// </summary>
  public static readonly string[] V2RequiredKeys = [ZarrFormatKey, ShapeKey, ChunksKey, DtypeKey];
  /// <summary>
  /// Provides the v 3 required keys value.
  /// </summary>
  public static readonly string[] V3RequiredKeys = [ZarrFormatKey, NodeTypeKey];
}
