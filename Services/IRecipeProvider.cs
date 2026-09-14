using SPPS2001D_Pro.Models;

namespace SPPS2001D_Pro.Services
{
    /// <summary>
    /// Provides power-supply recipes independently of where production data is stored.
    /// Implementations can read Excel/network files now and Plex later.
    /// </summary>
    public interface IRecipeProvider
    {
        Task<PowerSupplyRecipe?> GetRecipeAsync(
            string partNumber,
            string? revision = null,
            CancellationToken cancellationToken = default);
    }
}
