#pragma warning disable CS1591
namespace FileFormat.Zarr;

public static class ZarrConstants {

  public const string ZarrFormatKey = "zarr_format";
  public const string ShapeKey = "shape";
  public const string ChunksKey = "chunks";
  public const string DtypeKey = "dtype";
  public const string CompressorKey = "compressor";
  public const string FillValueKey = "fill_value";
  public const string OrderKey = "order";
  public const string FiltersKey = "filters";
  public const string DimensionSeparatorKey = "dimension_separator";

  public const string NodeTypeKey = "node_type";
  public const string DataTypeKey = "data_type";
  public const string ChunkGridKey = "chunk_grid";
  public const string ChunkKeyEncodingKey = "chunk_key_encoding";
  public const string CodecsKey = "codecs";
  public const string AttributesKey = "attributes";
  public const string DimensionNamesKey = "dimension_names";

  public const string IdKey = "id";
  public const string NameKey = "name";
  public const string ConfigurationKey = "configuration";
  public const string ChunkShapeKey = "chunk_shape";

  public const string NodeTypeArray = "array";
  public const string NodeTypeGroup = "group";

  public static readonly string[] V2RequiredKeys = [ZarrFormatKey, ShapeKey, ChunksKey, DtypeKey];
  public static readonly string[] V3RequiredKeys = [ZarrFormatKey, NodeTypeKey];
}
