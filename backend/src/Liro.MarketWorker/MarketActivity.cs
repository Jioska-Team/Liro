namespace Liro.MarketWorker;

public sealed class MarketActivityEvidence
{
    public Guid ItemId { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public int CompletedDays { get; set; }
    public decimal WeeklySales { get; set; }
    public bool HasRecent { get; set; }
    public bool HasHistory { get; set; }

    // A partial week proves a minimum, not an exact daily average or inactivity.
    public decimal? ExactDailySales => CompletedDays == 7 ? WeeklySales / 7 : null;
    public string Activity => WeeklySales >= 700 ? "MuyAlta" : WeeklySales >= 70 ? "Alta"
        : WeeklySales >= 7 ? "Media" : CompletedDays == 7 ? (WeeklySales > 0 ? "Baja" : "Inactiva")
        : HasRecent ? "InciertaReciente" : HasHistory ? "SinVentasRecientes" : "SinHistorial";
}
