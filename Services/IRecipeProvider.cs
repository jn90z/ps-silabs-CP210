using SPPS2001D_Pro.Models;

namespace SPPS2001D_Pro.Services
{
    /// <summary>
    /// Provides power-supply recipes independently of where production data is stored.
    /// The unique part number is the recipe key.
    /// </summary>
    public interface IRecipeProvider
    {
        Task<PowerSupplyRecipe?> GetRecipeAsync(
            string partNumber,
            CancellationToken cancellationToken = default);
    }
}
