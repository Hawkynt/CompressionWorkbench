namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 512 x 17 class 2.
/// </summary>
public class Line512X17Class2 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        3, 3, 3, 14, 5, 4, 4, 11, 8, 6, 6, 10, 17, 12, 11, 17,
        6, 5, 5, 15, 5, 3, 4, 11, 8, 5, 5, 8, 16, 9, 10, 14,
        10, 8, 9, 17, 8, 6, 6, 13, 10, 7, 7, 10, 16, 11, 13, 14,
        17, 17, 17, 17, 17, 16, 16, 16, 16, 15, 16, 16, 16, 16, 16, 16
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
