namespace SmartcityBackend.src.Helpers;

/// <summary>
/// Sri Lanka domestic (D-1) electricity tariff — PUCSL "Decision on Electricity Tariffs", Annex 2,
/// effective 11 May 2026 (kept unchanged by the 31 July 2026 review). 30-day billing cycle, LKR.
///
///   0–60 kWh      : 0–30 @ 5.00, 31–60 @ 9.00              fixed 80 (≤30) / 210 (31–60)
///   61–180 kWh    : first 60 @ 14.00, 61–90 @ 20.00,
///                   91–120 @ 28.00, 121–180 @ 44.00        fixed 400 / 1,000 / 1,500 (by last block)
///   above 180 kWh : first 180 @ 32.50, above 180 @ 100.00  fixed 2,500
///
/// SSCL (Social Security Contribution Levy) 2.5% is added on the levy-inclusive value (÷ 0.975).
/// Update the numbers here when PUCSL revises the tariff.
/// </summary>
public static class CebTariff
{
    public const string Currency = "LKR";
    public const string TariffName = "CEB domestic tariff (PUCSL, effective 11 May 2026)";
    private const double SsclRate = 0.025;

    public sealed record Bill(
        double Units, double EnergyCharge, double FixedCharge, double Sscl, double Total);

    public static Bill Calculate(double units)
    {
        units = Math.Max(0, Math.Round(units));
        double energy, fixedCharge;

        if (units <= 60)
        {
            energy = Math.Min(units, 30) * 5.00
                   + Math.Max(0, units - 30) * 9.00;
            fixedCharge = units <= 30 ? 80 : 210;
        }
        else if (units <= 180)
        {
            energy = 60 * 14.00
                   + Block(units, 60, 90) * 20.00
                   + Block(units, 90, 120) * 28.00
                   + Block(units, 120, 180) * 44.00;
            fixedCharge = units <= 90 ? 400 : units <= 120 ? 1000 : 1500;
        }
        else
        {
            energy = 180 * 32.50 + (units - 180) * 100.00;
            fixedCharge = 2500;
        }

        var subtotal = energy + fixedCharge;
        var total = subtotal / (1 - SsclRate);
        return new Bill(units, Math.Round(energy, 2), fixedCharge,
                        Math.Round(total - subtotal, 2), Math.Round(total, 2));
    }

    /// <summary>
    /// Expected monthly units after weather load: +0.5% per °C above 25 °C (fans / A/C)
    /// and +0.5% per °C below 15 °C (heaters, hill country).
    /// </summary>
    public static double AdjustForTemperature(double units, double temperatureC)
    {
        var factor = temperatureC > 25 ? 1 + (temperatureC - 25) * 0.005
                   : temperatureC < 15 ? 1 + (15 - temperatureC) * 0.005
                   : 1;
        return Math.Round(units * factor);
    }

    private static double Block(double units, double from, double to)
        => Math.Max(0, Math.Min(units, to) - from);
}
