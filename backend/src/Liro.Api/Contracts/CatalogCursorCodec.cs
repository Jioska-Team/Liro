using System.Globalization;
using System.Text;
using System.Text.Json;
using Liro.Application.Catalog.Repositories;
using Microsoft.AspNetCore.WebUtilities;

namespace Liro.Api.Contracts;

// Version and scope bind the token to this endpoint's ordering. This is a position, not an authorization token.
public static class CatalogCursorCodec
{
    private const string Scope = "catalog-v1";

    public static string Encode(CatalogCursor cursor) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
        $"{Scope}|{(cursor.CreatedAtUtc is { } date ? date.ToString("O", CultureInfo.InvariantCulture) : "null")}|{cursor.Id:D}"));

    public static bool TryDecode(string? token, out CatalogCursor? cursor)
    {
        cursor = null;
        if (token is null)
            return true;
        if (token.Length is < 1 or > 256)
            return false;
        try
        {
            var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)).Split('|');
            if (parts.Length != 3 || parts[0] != Scope || !Guid.TryParseExact(parts[2], "D", out var id) || id == Guid.Empty)
                return false;
            DateTime? created = null;
            if (parts[1] != "null")
            {
                if (!DateTime.TryParseExact(parts[1], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                    || date.Kind != DateTimeKind.Utc)
                    return false;
                created = date;
            }
            cursor = new(created, id);
            return Encode(cursor) == token;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private const string FilteredScope = "catalog-v2";
    private sealed record FilteredCursor(string Scope, CatalogBrowseOptions Options, CatalogCursor Position);

    public static string Encode(CatalogCursor cursor, CatalogBrowseOptions options) =>
        options.IsDefault ? Encode(cursor) : WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(new FilteredCursor(FilteredScope, options, cursor)));

    public static bool TryDecode(string? token, CatalogBrowseOptions options, out CatalogCursor? cursor)
    {
        if (options.IsDefault)
            return TryDecode(token, out cursor);
        cursor = null;
        if (token is null)
            return true;
        if (token.Length is < 1 or > 8192)
            return false;
        try
        {
            var payload = JsonSerializer.Deserialize<FilteredCursor>(WebEncoders.Base64UrlDecode(token));
            if (payload is null || payload.Scope != FilteredScope || payload.Options != options
                || payload.Position is not { } position || position.Id == Guid.Empty
                || !ValidPosition(position, options.Sort) || Encode(position, options) != token)
                return false;
            cursor = position;
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidPosition(CatalogCursor cursor, CatalogSort sort) => sort switch
    {
        CatalogSort.Created => (cursor.CreatedAtUtc is null || cursor.CreatedAtUtc.Value.Kind == DateTimeKind.Utc)
            && cursor.Name is null && cursor.Value is null,
        CatalogSort.Name => cursor.Name is { Length: <= 400 } && !cursor.Name.Contains('\0')
            && cursor.CreatedAtUtc is null && cursor.Value is null,
        CatalogSort.Price or CatalogSort.Rap or CatalogSort.Sales => cursor.CreatedAtUtc is null && cursor.Name is null,
        _ => false
    };
}
