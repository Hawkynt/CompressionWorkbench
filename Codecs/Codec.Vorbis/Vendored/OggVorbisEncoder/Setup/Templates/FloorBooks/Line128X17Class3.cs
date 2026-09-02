namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 128 x 17 class 3.
/// </summary>
public class Line128X17Class3 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
        3, 6, 10, 17, 4, 8, 11, 20, 8, 10, 11, 20, 20, 20, 20, 20,
        2, 4, 8, 18, 4, 6, 8, 17, 7, 8, 10, 20, 20, 17, 20, 20,
        3, 5, 8, 17, 3, 4, 6, 17, 8, 8, 10, 17, 17, 12, 16, 20,
        13, 13, 15, 20, 10, 10, 12, 20, 15, 14, 15, 20, 20, 20, 19, 19
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
