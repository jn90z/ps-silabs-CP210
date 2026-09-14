using System;
using System.Text.RegularExpressions;

namespace SPPS2001D_Pro.Services
{
    public static class Protocol
    {
        // Setpoint encoding stays 6-digit only: XXX.XXX -> XXXXXX
        public static string Encode6(double value)
        {
            if (value < 0) value = 0;
            // for 200V max, uncomment below. may as well leave open for 
            //if (value > 200.0) value = 200.0;
            return value.ToString("000.000", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "");
        }

        public static double ParseVoltage(string frame)
        {
            var m6 = Regex.Match(frame, @"^<12(\d{6})");
            if (m6.Success) return int.Parse(m6.Groups[1].Value) / 1000.0;

            var m4 = Regex.Match(frame, @"^<12(\d{4})");
            if (m4.Success) return int.Parse(m4.Groups[1].Value) / 100.0;

            return 0.0;
        }

        public static (string Mode, double A) ParseCurrent(string frame)
        {
            var m6 = Regex.Match(frame, @"^<([1C])4(\d{6})", RegexOptions.IgnoreCase);
            if (m6.Success)
            {
                string mode = m6.Groups[1].Value.Equals("1", StringComparison.OrdinalIgnoreCase) ? "CV" : "CC";
                return (mode, int.Parse(m6.Groups[2].Value) / 1000.0);
            }

            var m4 = Regex.Match(frame, @"^<([1C])4(\d{4})", RegexOptions.IgnoreCase);
            if (m4.Success)
            {
                string mode = m4.Groups[1].Value.Equals("1", StringComparison.OrdinalIgnoreCase) ? "CV" : "CC";
                return (mode, int.Parse(m4.Groups[2].Value) / 1000.0);
            }

            return ("?", 0.0);
        }
    }
}
