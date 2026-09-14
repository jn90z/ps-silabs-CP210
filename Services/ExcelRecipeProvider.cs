using ClosedXML.Excel;
using SPPS2001D_Pro.Models;
using System.Globalization;

namespace SPPS2001D_Pro.Services
{
    /// <summary>
    /// Read-only recipe provider for an .xlsx workbook on a local or UNC network path.
    /// Expected headers: PartNumber, Revision, Voltage, Current, MaxVoltage, MaxCurrent.
    /// Revision and maximum-limit columns are optional.
    /// </summary>
    public sealed class ExcelRecipeProvider : IRecipeProvider
    {
        private readonly string _workbookPath;
        private readonly string? _worksheetName;

        public ExcelRecipeProvider(string workbookPath, string? worksheetName = null)
        {
            if (string.IsNullOrWhiteSpace(workbookPath))
                throw new ArgumentException("Workbook path is required.", nameof(workbookPath));

            _workbookPath = workbookPath;
            _worksheetName = worksheetName;
        }

        public Task<PowerSupplyRecipe?> GetRecipeAsync(
            string partNumber,
            string? revision = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                throw new ArgumentException("Part number is required.", nameof(partNumber));

            return Task.Run(() => ReadRecipe(partNumber.Trim(), revision?.Trim(), cancellationToken), cancellationToken);
        }

        private PowerSupplyRecipe? ReadRecipe(string partNumber, string? revision, CancellationToken ct)
        {
            if (!File.Exists(_workbookPath))
                throw new FileNotFoundException("Recipe workbook was not found.", _workbookPath);

            using var workbook = new XLWorkbook(_workbookPath, XLEventTracking.Disabled);
            var worksheet = string.IsNullOrWhiteSpace(_worksheetName)
                ? workbook.Worksheets.FirstOrDefault()
                : workbook.Worksheets.FirstOrDefault(w =>
                    string.Equals(w.Name, _worksheetName, StringComparison.OrdinalIgnoreCase));

            if (worksheet is null)
                throw new InvalidDataException("Recipe workbook does not contain the requested worksheet.");

            var headerRow = worksheet.FirstRowUsed()
                ?? throw new InvalidDataException("Recipe worksheet is empty.");

            var headers = headerRow.CellsUsed()
                .ToDictionary(
                    c => c.GetString().Trim(),
                    c => c.Address.ColumnNumber,
                    StringComparer.OrdinalIgnoreCase);

            int partCol = RequireColumn(headers, "PartNumber");
            int voltageCol = RequireColumn(headers, "Voltage");
            int currentCol = RequireColumn(headers, "Current");
            headers.TryGetValue("Revision", out int revisionCol);
            headers.TryGetValue("MaxVoltage", out int maxVoltageCol);
            headers.TryGetValue("MaxCurrent", out int maxCurrentCol);

            foreach (var row in worksheet.RowsUsed().Skip(1))
            {
                ct.ThrowIfCancellationRequested();

                string rowPart = row.Cell(partCol).GetString().Trim();
                if (!string.Equals(rowPart, partNumber, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? rowRevision = revisionCol > 0
                    ? NullIfBlank(row.Cell(revisionCol).GetString())
                    : null;

                // If a revision was requested, require an exact revision match.
                if (!string.IsNullOrWhiteSpace(revision) &&
                    !string.Equals(rowRevision, revision, StringComparison.OrdinalIgnoreCase))
                    continue;

                double voltage = ReadRequiredDouble(row.Cell(voltageCol), "Voltage", row.RowNumber());
                double current = ReadRequiredDouble(row.Cell(currentCol), "Current", row.RowNumber());
                double? maxVoltage = maxVoltageCol > 0 ? ReadOptionalDouble(row.Cell(maxVoltageCol)) : null;
                double? maxCurrent = maxCurrentCol > 0 ? ReadOptionalDouble(row.Cell(maxCurrentCol)) : null;

                if (voltage < 0 || current < 0)
                    throw new InvalidDataException($"Recipe row {row.RowNumber()} contains a negative voltage/current value.");
                if (maxVoltage.HasValue && voltage > maxVoltage.Value)
                    throw new InvalidDataException($"Recipe row {row.RowNumber()} voltage exceeds MaxVoltage.");
                if (maxCurrent.HasValue && current > maxCurrent.Value)
                    throw new InvalidDataException($"Recipe row {row.RowNumber()} current exceeds MaxCurrent.");

                return new PowerSupplyRecipe(rowPart, rowRevision, voltage, current, maxVoltage, maxCurrent);
            }

            return null;
        }

        private static int RequireColumn(IReadOnlyDictionary<string, int> headers, string name)
            => headers.TryGetValue(name, out int column)
                ? column
                : throw new InvalidDataException($"Recipe worksheet is missing required column '{name}'.");

        private static double ReadRequiredDouble(IXLCell cell, string columnName, int rowNumber)
            => ReadOptionalDouble(cell)
               ?? throw new InvalidDataException($"Recipe row {rowNumber} has no valid {columnName} value.");

        private static double? ReadOptionalDouble(IXLCell cell)
        {
            if (cell.IsEmpty()) return null;
            if (cell.TryGetValue<double>(out double numeric)) return numeric;

            string text = cell.GetString().Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariant))
                return invariant;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double current))
                return current;

            return null;
        }

        private static string? NullIfBlank(string value)
        {
            string trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
