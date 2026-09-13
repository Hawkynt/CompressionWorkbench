namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 128 x 17 0 sub 0.
/// </summary>
public class Line128X17_0Sub0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
        5, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 5,
        7, 5, 7, 5, 7, 5, 7, 5, 7, 5, 7, 5, 8, 5, 8, 5,
        8, 5, 8, 5, 8, 6, 8, 6, 8, 6, 9, 6, 9, 6, 9, 6,
        9, 6, 9, 7, 9, 7, 9, 7, 9, 7, 10, 7, 10, 8, 10, 8,
        10, 8, 10, 8, 10, 8, 11, 8, 11, 8, 11, 8, 11, 8, 11, 9,
        12, 9, 12, 9, 12, 9, 12, 9, 12, 10, 12, 10, 13, 11, 13, 11,
        14, 12, 14, 13, 15, 14, 16, 14, 17, 15, 18, 16, 20, 20, 20, 20,
        20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = CodeBookMapType.None;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;
    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = null;
}
