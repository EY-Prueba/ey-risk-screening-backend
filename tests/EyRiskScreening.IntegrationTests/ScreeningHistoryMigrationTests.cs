using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningHistoryMigrationTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_HistoryMigrationTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SchemaTablesAndInitialIdentityAreIntact()
    {
        await using var connection = await OpenConnectionAsync();
        var schema = await ExecuteScalarAsync(
            connection,
            "SELECT SCHEMA_NAME(SCHEMA_ID(N'screening'))");
        Assert.Equal("screening", schema);

        var historyTables = await ReadStringsAsync(
            connection,
            """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'screening'
            ORDER BY TABLE_NAME
            """);
        Assert.Equal(
            ["ScreeningMatches", "ScreeningRuns", "ScreeningSourceResults"],
            historyTables);

        var identityTables = await ReadStringsAsync(
            connection,
            """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME LIKE 'AspNet%'
            ORDER BY TABLE_NAME
            """);
        Assert.Equal(
            [
                "AspNetRoleClaims",
                "AspNetRoles",
                "AspNetUserClaims",
                "AspNetUserLogins",
                "AspNetUserRoles",
                "AspNetUsers",
                "AspNetUserTokens",
            ],
            identityTables);

        var migrations = await ReadStringsAsync(
            connection,
            """
            SELECT [MigrationId]
            FROM [__EFMigrationsHistory]
            ORDER BY [MigrationId]
            """);
        Assert.Equal(2, migrations.Count);
        Assert.Equal("20260722101752_InitialIdentity", migrations[0]);
        Assert.EndsWith("_AddScreeningHistory", migrations[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColumnsPrimaryKeysAndForeignKeysMatchTheAggregate()
    {
        await using var connection = await OpenConnectionAsync();
        var lengths = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(TABLE_NAME, '|', COLUMN_NAME, '|', DATA_TYPE, '|',
                CHARACTER_MAXIMUM_LENGTH)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'screening'
              AND COLUMN_NAME IN (
                'EntityName',
                'NormalizedEntityName',
                'ReferenceId',
                'Name',
                'NormalizedName',
                'ErrorMessage')
            ORDER BY TABLE_NAME, COLUMN_NAME
            """);
        Assert.Equal(
            [
                $"ScreeningMatches|Name|nvarchar|{ScreeningHistoryLimits.MatchNameSqlLength}",
                $"ScreeningMatches|NormalizedName|nvarchar|{ScreeningHistoryLimits.MatchNameSqlLength}",
                $"ScreeningMatches|ReferenceId|nvarchar|{ScreeningHistoryLimits.ReferenceIdSqlLength}",
                $"ScreeningRuns|EntityName|nvarchar|{ScreeningHistoryLimits.EntityNameSqlLength}",
                $"ScreeningRuns|NormalizedEntityName|nvarchar|{ScreeningHistoryLimits.EntityNameSqlLength}",
                $"ScreeningSourceResults|ErrorMessage|nvarchar|{ScreeningHistoryLimits.ErrorMessageSqlLength}",
            ],
            lengths);

        var primaryKeys = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(OBJECT_NAME(parent_object_id), '|', name)
            FROM sys.key_constraints
            WHERE [type] = 'PK'
              AND OBJECT_SCHEMA_NAME(parent_object_id) = 'screening'
            ORDER BY OBJECT_NAME(parent_object_id)
            """);
        Assert.Equal(
            [
                "ScreeningMatches|PK_ScreeningMatches",
                "ScreeningRuns|PK_ScreeningRuns",
                "ScreeningSourceResults|PK_ScreeningSourceResults",
            ],
            primaryKeys);

        var foreignKeys = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(
                OBJECT_NAME(fk.parent_object_id), '|',
                pc.name COLLATE DATABASE_DEFAULT, '|',
                OBJECT_SCHEMA_NAME(fk.referenced_object_id), '.',
                OBJECT_NAME(fk.referenced_object_id), '|',
                rc.name COLLATE DATABASE_DEFAULT, '|',
                fk.delete_referential_action_desc)
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS pc
                ON pc.object_id = fk.parent_object_id
                AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS rc
                ON rc.object_id = fk.referenced_object_id
                AND rc.column_id = fkc.referenced_column_id
            WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = 'screening'
            ORDER BY OBJECT_NAME(fk.parent_object_id)
            """);
        Assert.Equal(
            [
                "ScreeningMatches|ScreeningSourceResultId|screening.ScreeningSourceResults|ScreeningSourceResultId|CASCADE",
                "ScreeningRuns|UserId|dbo.AspNetUsers|Id|NO_ACTION",
                "ScreeningSourceResults|RunId|screening.ScreeningRuns|RunId|CASCADE",
            ],
            foreignKeys);
    }

    [Fact]
    public async Task IndexesPreserveUniquenessAndHistoryAccessPatterns()
    {
        await using var connection = await OpenConnectionAsync();
        var indexes = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(
                i.name, '|',
                i.is_unique, '|',
                STRING_AGG(
                    CONCAT(c.name, ':', ic.is_descending_key),
                    ',') WITHIN GROUP (ORDER BY ic.key_ordinal))
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = i.object_id
                AND c.column_id = ic.column_id
            WHERE OBJECT_SCHEMA_NAME(i.object_id) = 'screening'
              AND i.is_primary_key = 0
              AND ic.is_included_column = 0
            GROUP BY i.name, i.is_unique
            ORDER BY i.name
            """);
        Assert.Equal(
            [
                "IX_ScreeningMatches_ScreeningSourceResultId_SortOrder|1|ScreeningSourceResultId:0,SortOrder:0",
                "IX_ScreeningRuns_RequestedAtUtc|0|RequestedAtUtc:1",
                "IX_ScreeningRuns_UserId_RequestedAtUtc|0|UserId:0,RequestedAtUtc:1",
                "IX_ScreeningSourceResults_RunId_Source|1|RunId:0,Source:0",
            ],
            indexes);
    }

    [Fact]
    public async Task CheckConstraintsEnforceEnumsOutcomesCountsScoresAndJson()
    {
        await using var connection = await OpenConnectionAsync();
        var rows = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(name, '|', definition)
            FROM sys.check_constraints
            WHERE OBJECT_SCHEMA_NAME(parent_object_id) = 'screening'
            ORDER BY name
            """);
        var checks = rows.ToDictionary(
            row => row[..row.IndexOf('|', StringComparison.Ordinal)],
            row => Compact(row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..]));
        Assert.Equal(
            [
                "CK_ScreeningMatches_ExactScore",
                "CK_ScreeningMatches_FieldsJson",
                "CK_ScreeningMatches_Scores",
                "CK_ScreeningMatches_SortOrder",
                "CK_ScreeningRuns_Counts",
                "CK_ScreeningRuns_Status",
                "CK_ScreeningRuns_Timestamps",
                "CK_ScreeningRuns_TotalDurationMs",
                "CK_ScreeningSourceResults_Counts",
                "CK_ScreeningSourceResults_DurationMs",
                "CK_ScreeningSourceResults_ErrorCode",
                "CK_ScreeningSourceResults_MatchThreshold",
                "CK_ScreeningSourceResults_Outcome",
                "CK_ScreeningSourceResults_Source",
                "CK_ScreeningSourceResults_Status",
                "CK_ScreeningSourceResults_UnsuccessfulCounts",
            ],
            checks.Keys.Order().ToArray());

        AssertEnumValues(checks["CK_ScreeningRuns_Status"], [0, 1, 2]);
        AssertEnumValues(checks["CK_ScreeningSourceResults_Source"], [0, 1, 2]);
        AssertEnumValues(
            checks["CK_ScreeningSourceResults_Status"],
            [0, 1, 2, 3]);
        AssertEnumValues(
            checks["CK_ScreeningSourceResults_ErrorCode"],
            [0, 1, 2, 3]);
        Assert.Contains(
            "[CompletedAtUtc]>=[RequestedAtUtc]",
            checks["CK_ScreeningRuns_Timestamps"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[TotalReturnedResults]<=[TotalHits]",
            checks["CK_ScreeningRuns_Counts"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[ReturnedResults]<=[Hits]",
            checks["CK_ScreeningSourceResults_Counts"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[DurationMs]>=(0)",
            checks["CK_ScreeningSourceResults_DurationMs"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[TotalDurationMs]>=(0)",
            checks["CK_ScreeningRuns_TotalDurationMs"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[Status]=(0)",
            checks["CK_ScreeningSourceResults_Outcome"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[ErrorMessage]ISNULL",
            checks["CK_ScreeningSourceResults_Outcome"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[ErrorMessage]ISNOTNULL",
            checks["CK_ScreeningSourceResults_Outcome"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[OverallScore]>=(0)",
            checks["CK_ScreeningMatches_Scores"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[OverallScore]<=(100)",
            checks["CK_ScreeningMatches_Scores"],
            StringComparison.Ordinal);
        Assert.Contains(
            "[IsExactMatch]=(0)OR[OverallScore]=(100)",
            checks["CK_ScreeningMatches_ExactScore"],
            StringComparison.Ordinal);
        Assert.Contains(
            "ISJSON([FieldsJson])=(1)",
            checks["CK_ScreeningMatches_FieldsJson"],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DATALENGTH([FieldsJson])<=(131072)",
            checks["CK_ScreeningMatches_FieldsJson"],
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<string?> ExecuteScalarAsync(
        SqlConnection connection,
        string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);
        return value as string;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqlConnection connection,
        string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void AssertEnumValues(
        string definition,
        IReadOnlyList<int> expectedValues)
    {
        foreach (var value in expectedValues)
        {
            Assert.Contains(
                $"({value})",
                definition,
                StringComparison.Ordinal);
        }

        var unexpectedValue = expectedValues.Max() + 1;
        Assert.DoesNotContain(
            $"({unexpectedValue})",
            definition,
            StringComparison.Ordinal);
    }

    private static string Compact(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
