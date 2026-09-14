namespace SPPS2001D_Pro.Models
{
    /// <summary>
    /// Power-supply settings associated with a production part/revision.
    /// The source may be Excel today and Plex in the future.
    /// </summary>
    public sealed record PowerSupplyRecipe(
        string PartNumber,
        string? Revision,
        double Voltage,
        double Current,
        double? MaxVoltage = null,
        double? MaxCurrent = null);
}
