namespace OggVorbisEncoder.Setup;

/// <summary>
/// Specifies code book map type values.
/// </summary>
public enum CodeBookMapType : int
{
        /// <summary>
    /// Specifies that no option is selected.
    /// </summary>
None = 0,
        /// <summary>
    /// Specifies the implicit option.
    /// </summary>
Implicit = 1, // implicitly populated values from map column
        /// <summary>
    /// Specifies the listed option.
    /// </summary>
Listed = 2 // listed arbitrary values
}
