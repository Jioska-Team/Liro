using System.Globalization;
using System.Text.Json;

namespace Liro.Infrastructure.Market;

public sealed class CatalogoGeneral
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Tipo { get; set; } = "";
    public string Clave { get; set; } = "";
    public string Valor { get; set; } = "";
    public string TipoDato { get; set; } = "Texto";
    public string Descripcion { get; set; } = "";
    public bool Activo
    {
        get; set;
    }
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public DateTime FechaModificacion { get; set; } = DateTime.UtcNow;
    public string CreadoPor { get; set; } = "sistema";
    public string ModificadoPor { get; set; } = "sistema";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Tipo) || Tipo.Length > 80 || string.IsNullOrWhiteSpace(Clave) || Clave.Length > 120
            || Valor.Length > 16000 || Descripcion.Length > 1000 || string.IsNullOrWhiteSpace(CreadoPor)
            || string.IsNullOrWhiteSpace(ModificadoPor) || CreadoPor.Length > 120 || ModificadoPor.Length > 120
            || FechaCreacion.Kind != DateTimeKind.Utc || FechaModificacion.Kind != DateTimeKind.Utc || FechaModificacion < FechaCreacion)
            throw new ArgumentException("Registro de configuración inválido.");
        var valid = TipoDato switch
        {
            "Texto" => true,
            "Entero" => long.TryParse(Valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "Decimal" => decimal.TryParse(Valor, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            "Booleano" => bool.TryParse(Valor, out _),
            "Json" => IsJson(Valor),
            _ => false
        };
        if (!valid)
            throw new ArgumentException("El valor no coincide con TipoDato.");
    }
    private static bool IsJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

// RAP has a separate observation timestamp from existing catalog prices.
public sealed class MarketWatch
{
    public Guid ItemId
    {
        get; set;
    }
    public DateTime NextCheckAtUtc
    {
        get; set;
    }
    public DateTime? LastAttemptAtUtc
    {
        get; set;
    }
    public DateTime? LastSuccessAtUtc
    {
        get; set;
    }
    public Guid? LeaseId
    {
        get; set;
    }
    public DateTime? LeaseUntilUtc
    {
        get; set;
    }
    public int ConsecutiveFailures
    {
        get; set;
    }
    public string? LastErrorCode
    {
        get; set;
    }
    public decimal? RecentAveragePrice
    {
        get; set;
    }
    public DateTime? RapObservedAtUtc
    {
        get; set;
    }
    public decimal? DailySales
    {
        get; set;
    }
    public string Activity { get; set; } = "SinDatos";
}

public sealed class MarketHistoryPoint
{
    public Guid ItemId
    {
        get; set;
    }
    public string Source { get; set; } = "marketplace-sales";
    public string Metric { get; set; } = "";
    public DateTime PeriodUtc
    {
        get; set;
    }
    public decimal Value
    {
        get; set;
    }
    public DateTime ObservedAtUtc
    {
        get; set;
    }
}

public sealed class MarketRapSnapshot
{
    public Guid ItemId
    {
        get; set;
    }
    public DateTime ObservedAtUtc
    {
        get; set;
    }
    public decimal Value
    {
        get; set;
    }
}

public sealed class MarketDashboardSnapshot
{
    public DateTime BucketUtc { get; set; }
    public int LimitedsCount { get; set; }
    public string Payload { get; set; } = "";
}
