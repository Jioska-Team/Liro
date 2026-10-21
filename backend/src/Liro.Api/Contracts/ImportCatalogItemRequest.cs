using System.ComponentModel.DataAnnotations;

namespace Liro.Api.Contracts;

public sealed record ImportCatalogItemRequest(
    [Required, RegularExpression("^(Asset|Bundle)$")] string ItemType,
    [Range(typeof(long), "1", "9223372036854775807")] long RobloxId);
