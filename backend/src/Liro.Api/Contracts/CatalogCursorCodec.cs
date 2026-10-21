using System.Globalization;
using System.Text;
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
}
