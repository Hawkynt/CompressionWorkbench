namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a mapping template.
/// </summary>
public class MappingTemplate : IMappingTemplate
{
        /// <summary>
    /// Initializes a new instance of <see cref="MappingTemplate"/>.
    /// </summary>
public MappingTemplate(
        Mapping[] mapping,
        IResidueTemplate[] residueTemplate)
    {
        Mapping = mapping;
        ResidueTemplate = residueTemplate;
    }

        /// <summary>
    /// Gets the mapping.
    /// </summary>
public Mapping[] Mapping { get; }
        /// <summary>
    /// Gets the residue template.
    /// </summary>
public IResidueTemplate[] ResidueTemplate { get; }
}
