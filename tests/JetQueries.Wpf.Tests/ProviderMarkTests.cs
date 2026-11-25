using System.Windows.Media;
using JetQueries.Provider.Oracle;
using JetQueries.Provider.PostgreSql;
using JetQueries.Provider.Redis;
using JetQueries.Provider.Sqlite;
using JetQueries.Provider.SqlServer;
using JetQueries.Sdk;

namespace JetQueries.Wpf.Tests;

/// <summary>Every built-in provider's logo is valid path data that fills its canvas.</summary>
public sealed class ProviderMarkTests
{
    public static TheoryData<string> Providers => ["sqlserver", "oracle", "postgresql", "sqlite", "redis"];

    [Theory]
    [MemberData(nameof(Providers))]
    public void A_logo_parses_and_fits_the_24_pixel_canvas(string id)
    {
        IDatabaseProvider provider = id switch
        {
            "sqlserver" => new SqlServerProvider(),
            "oracle" => new OracleProvider(),
            "postgresql" => new PostgreSqlProvider(),
            "sqlite" => new SqliteProvider(),
            _ => new RedisProvider(),
        };
        var appearance = Assert.IsAssignableFrom<IProviderAppearance>(provider);
        var bounds = Geometry.Parse(appearance.IconPathData).Bounds;
        Assert.True(bounds.Width > 12 && bounds.Height > 6, $"{id}: {bounds}");
        Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01, $"{id}: {bounds}");
        Assert.Matches("^#[0-9A-F]{6}$", appearance.AccentColor);
    }
}
