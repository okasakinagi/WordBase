using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WordBase.Data;

internal static class Sql
{
    public const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public static string ToIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(IsoFormat, CultureInfo.InvariantCulture);

    public static string? ToIso(DateTimeOffset? value) =>
        value is null ? null : ToIso(value.Value);

    public static DateTimeOffset? ToDate(string? text) =>
        string.IsNullOrEmpty(text)
            ? null
            : DateTimeOffset.ParseExact(text, IsoFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static T? GetNullable<T>(this SqliteDataReader reader, string column) where T : class
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetFieldValue<T>(index);
    }

    public static long? GetNullableLong(this SqliteDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetInt64(index);
    }

    public static double? GetNullableDouble(this SqliteDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetDouble(index);
    }
}
