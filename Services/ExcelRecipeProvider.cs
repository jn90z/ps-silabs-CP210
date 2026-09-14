using ClosedXML.Excel;
using SPPS2001D_Pro.Models;
using System.Globalization;
using System.IO;

namespace SPPS2001D_Pro.Services
{
    /// <summary>
    /// Read-only recipe provider for an .xlsx workbook on a local or UNC network path.
    /// Expected headers: PartNumber, Voltage, Current, MaxVoltage, MaxCurrent.
    /// Maximum-limit columns are optional. PartNumber is the unique recipe key.
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
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                throw new ArgumentException("Part number is required.", nameof(partNumber));
            return Task.Run(() => ReadRecipe(partNumber.Trim(), cancellationToken), cancellationToken);
        }

        private PowerSupplyRecipe? ReadRecipe(string partNumber, CancellationToken ct)
        {
            if (!File.Exists(_workbookPath))
                throw new FileNotFoundException("Recipe workbook was not found.", _workbookPath);

            using var workbook = new XLWorkbook(_workbookPath);
            var worksheet = string.IsNullOrWhiteSpace(_worksheetName)
                ? workbook.Worksheets.FirstOrDefault()
                : workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name, _worksheetName, StringComparison.OrdinalIgnoreCase));

            if (worksheet is null)
                throw new InvalidDataException("Recipe workbook does not contain the requested worksheet.");

            var headerRow = worksheet.FirstRowUsed() ?? throw new InvalidDataException("Recipe worksheet is empty.");
            var headers = headerRow.CellsUsed().ToDictionary(
                c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

            int partCol = RequireColumn(headers, "PartNumber");
            int voltageCol = RequireColumn(headers, "Voltage");
            int currentCol = RequireColumn(headers, "Current");
            headers.TryGetValue("MaxVoltage", out int maxVoltageCol);
            headers.TryGetValue("MaxCurrent", out int maxCurrentCol);

            PowerSupplyRecipe? match = null;
            foreach (var row in worksheet.RowsUsed().Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                string rowPart = row.Cell(partCol).GetString().Trim();
                if (!string.Equals(rowPart, partNumber, StringComparison.OrdinalIgnoreCase)) continue;

                if (match is not null)
                    throw new InvalidDataException($"PartNumber '{partNumber}' appears more than once in the recipe worksheet. Part numbers must be unique.");

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

                match = new PowerSupplyRecipe(rowPart, voltage, current, maxVoltage, maxCurrent);
            }
            return match;
        }

        private static int RequireColumn(IReadOnlyDictionary<string, int> headers, string name)
            => headers.TryGetValue(name, out int column) ? column : throw new InvalidDataException($"Recipe worksheet is missing required column '{name}'.");

        private static double ReadRequiredDouble(IXLCell cell, string columnName, int rowNumber)
            => ReadOptionalDouble(cell) ?? throw new InvalidDataException($"Recipe row {rowNumber} has no valid {columnName} value.");

        private static double? ReadOptionalDouble(IXLCell cell)
        {
            if (cell.IsEmpty()) return null;
            if (cell.TryGetValue<double>(out double numeric)) return numeric;
            string text = cell.GetString().Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariant)) return invariant;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double current)) return current;
            return null;
        }
    }
}
