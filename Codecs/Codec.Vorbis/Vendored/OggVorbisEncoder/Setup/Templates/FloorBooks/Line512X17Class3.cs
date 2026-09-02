namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 512 x 17 class 3.
/// </summary>
public class Line512X17Class3 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        2, 4, 6, 17, 4, 5, 7, 17, 8, 7, 10, 17, 17, 17, 17, 17,
        3, 4, 6, 15, 3, 3, 6, 15, 7, 6, 9, 17, 17, 17, 17, 17,
        6, 8, 10, 17, 6, 6, 8, 16, 9, 8, 10, 17, 17, 15, 16, 17,
        17, 17, 17, 17, 12, 15, 15, 16, 12, 15, 15, 16, 16, 16, 16, 16
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
