using Liro.Domain.Catalog.Entities;
namespace Liro.Application.Catalog.Services;

public sealed record CatalogImportOutcome(Item? Item, bool Added);
