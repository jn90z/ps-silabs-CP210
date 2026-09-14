namespace SPPS2001D_Pro.Models
{
    /// <summary>
    /// Power-supply settings associated with a unique production part number.
    /// The source may be Excel today and Plex in the future.
    /// </summary>
    public sealed record PowerSupplyRecipe(
        string PartNumber,
        double Voltage,
        double Current,
        double? MaxVoltage = null,
        double? MaxCurrent = null);
}
