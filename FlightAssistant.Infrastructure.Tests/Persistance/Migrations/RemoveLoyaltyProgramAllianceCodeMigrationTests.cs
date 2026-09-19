using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Migrations;

/// <summary>
/// #504: the migration that drops <c>loyalty_programs.AllianceCode</c> first copies each program's
/// alliance onto its airline. The guards that cannot be read off the SQL are pinned here: an
/// alliance already on the airline is never overwritten, a code naming no alliance is discarded
/// rather than failing the migration, a disagreement between two programs of one airline is settled
/// by lowest program id, that tiebreak only considers programs whose code is actually copyable — an
/// invalid code on the lowest-id program must not veto a valid code held by a higher-id sibling —
/// and a valid code stored in the wrong case or with surrounding whitespace is normalised before
/// matching, so it is copied rather than discarded alongside genuine junk.
///
/// <para>This class owns its own container instead of joining <see cref="TestSupport.PostgresCollection"/>:
/// the shared fixture hands out a database already migrated to the latest migration, and this test
/// needs one at the migration before ours so it can write the column that is about to be dropped.</para>
/// </summary>
public sealed class RemoveLoyaltyProgramAllianceCodeMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private FlightAssistantDbContext _db = default!;
    private string _ourMigration = default!;
    private string _previousMigration = default!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _db = new FlightAssistantDbContext(options);

        var all = _db.Database.GetMigrations().ToList();
        _ourMigration = all.Single(m => m.EndsWith("RemoveLoyaltyProgramAllianceCode"));
        _previousMigration = all[all.IndexOf(_ourMigration) - 1];
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Up_MovesEachProgramsAllianceOntoItsAirline()
    {
        var migrator = _db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(_previousMigration);

        await ExecuteAsync("""
            INSERT INTO alliances ("Code", "Name") VALUES ('OW', 'oneworld'), ('SA', 'Star Alliance'), ('ST', 'SkyTeam');
            INSERT INTO countries ("Code", "Name") VALUES ('GB', 'United Kingdom') ON CONFLICT DO NOTHING;

            INSERT INTO airlines ("Iata", "Icao", "Name", "CountryCode", "AllianceCode")
            VALUES ('BA', 'BAW', 'British Airways', 'GB', NULL),      -- takes its program's alliance
                   ('LH', 'DLH', 'Lufthansa',       'GB', 'SA'),      -- keeps its own
                   ('XX', 'XXX', 'Junk Air',        'GB', NULL),      -- program's code is not an alliance
                   ('YY', 'YYY', 'Tie Air',         'GB', NULL),      -- two valid programs disagree
                   ('ZZ', 'ZZZ', 'Rescue Air',      'GB', NULL),      -- lowest-id program is junk, higher is valid
                   ('WW', 'WWW', 'Case Air',        'GB', NULL);      -- valid code, badly cased and padded

            INSERT INTO loyalty_program_kinds ("Code", "Name") VALUES ('FF', 'Frequent Flyer');

            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT v.code, v.name, k."Id", a."Id", v.alliance
            FROM (VALUES
                    ('BA-EXEC', 'BA Executive Club', 'BA', 'OW'),
                    ('LH-MM',   'Miles & More',      'LH', 'OW'),
                    ('XX-ONE',  'Junk Program',      'XX', 'STAR'),
                    ('WW-ONE',  'Case Club',         'WW', '  ow  ')
                 ) AS v(code, name, iata, alliance)
            JOIN airlines a ON a."Iata" = v.iata
            CROSS JOIN loyalty_program_kinds k
            WHERE k."Code" = 'FF';
            """);

        // The tie case, inserted as two statements so the ids are unambiguous: the identity column
        // hands the first INSERT the lower id, and that row's alliance is the one the migration must
        // pick. Do NOT fold these into the VALUES list above -- an ORDER BY on an INSERT ... SELECT
        // does not promise identity values follow it, so which row won would rest on luck and on
        // how the codes happen to sort. Two statements promise it outright.
        await ExecuteAsync("""
            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT 'YY-EARLIER', 'Tie, inserted first', k."Id", a."Id", 'OW'
            FROM airlines a CROSS JOIN loyalty_program_kinds k
            WHERE a."Iata" = 'YY' AND k."Code" = 'FF';
            """);

        await ExecuteAsync("""
            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT 'YY-LATER', 'Tie, inserted second', k."Id", a."Id", 'SA'
            FROM airlines a CROSS JOIN loyalty_program_kinds k
            WHERE a."Iata" = 'YY' AND k."Code" = 'FF';
            """);

        // The rescue case: the lowest-id program of ZZ holds the never-valid 'STAR', and only its
        // higher-id sibling holds a real alliance code. Two statements, same reason as above -- the
        // first INSERT must get the lower id so the ordering is not left to luck.
        await ExecuteAsync("""
            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT 'ZZ-JUNK', 'Rescue, lowest id, junk code', k."Id", a."Id", 'STAR'
            FROM airlines a CROSS JOIN loyalty_program_kinds k
            WHERE a."Iata" = 'ZZ' AND k."Code" = 'FF';
            """);

        await ExecuteAsync("""
            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT 'ZZ-VALID', 'Rescue, higher id, valid code', k."Id", a."Id", 'ST'
            FROM airlines a CROSS JOIN loyalty_program_kinds k
            WHERE a."Iata" = 'ZZ' AND k."Code" = 'FF';
            """);

        await migrator.MigrateAsync(_ourMigration);

        // The plain case: the program's alliance is now the airline's.
        Assert.Equal("OW", await AllianceOfAsync("BA"));

        // An alliance already on the airline outranks anything a program claims.
        Assert.Equal("SA", await AllianceOfAsync("LH"));

        // 'STAR' names no alliance, so the foreign key forbids copying it. It goes with the column.
        Assert.Null(await AllianceOfAsync("XX"));

        // The earlier-inserted program holds the lower id, so its OW wins over the later SA.
        Assert.Equal("OW", await AllianceOfAsync("YY"));

        // The lowest-id program's code is junk and does not name a real alliance; the tiebreak must
        // skip it and copy the higher-id program's valid code instead of vetoing the row entirely.
        // This is the case that fails against the pre-fix SQL, whose MIN("Id") subquery had no
        // validity guard: it would pick ZZ-JUNK's id, find its code invalid, and copy nothing.
        Assert.Equal("ST", await AllianceOfAsync("ZZ"));

        // A valid code stored badly cased and padded ('  ow  ') is normalised before matching, so
        // it is copied rather than discarded like a junk code would be.
        Assert.Equal("OW", await AllianceOfAsync("WW"));

        // And the column is gone.
        Assert.Equal(0L, await ScalarAsync("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = 'loyalty_programs' AND column_name = 'AllianceCode';
            """));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> AllianceOfAsync(string iata)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT "AllianceCode" FROM airlines WHERE "Iata" = @iata;""", connection);
        command.Parameters.AddWithValue("iata", iata);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
