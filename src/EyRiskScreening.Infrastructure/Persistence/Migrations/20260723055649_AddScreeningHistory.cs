using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EyRiskScreening.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreeningHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "screening");

            migrationBuilder.CreateTable(
                name: "ScreeningRuns",
                schema: "screening",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    NormalizedEntityName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    TotalHits = table.Column<int>(type: "int", nullable: false),
                    TotalReturnedResults = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningRuns", x => x.RunId);
                    table.CheckConstraint("CK_ScreeningRuns_Counts", "[TotalHits] >= 0 AND [TotalReturnedResults] >= 0 AND [TotalReturnedResults] <= [TotalHits]");
                    table.CheckConstraint("CK_ScreeningRuns_Status", "[Status] IN (0, 1, 2)");
                    table.CheckConstraint("CK_ScreeningRuns_Timestamps", "[CompletedAtUtc] >= [RequestedAtUtc]");
                    table.CheckConstraint("CK_ScreeningRuns_TotalDurationMs", "[TotalDurationMs] >= 0");
                    table.ForeignKey(
                        name: "FK_ScreeningRuns_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningSourceResults",
                schema: "screening",
                columns: table => new
                {
                    ScreeningSourceResultId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    MatchThreshold = table.Column<byte>(type: "tinyint", nullable: false),
                    Hits = table.Column<int>(type: "int", nullable: false),
                    ReturnedResults = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCode = table.Column<byte>(type: "tinyint", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningSourceResults", x => x.ScreeningSourceResultId);
                    table.CheckConstraint("CK_ScreeningSourceResults_Counts", "[Hits] >= 0 AND [ReturnedResults] >= 0 AND [ReturnedResults] <= [Hits]");
                    table.CheckConstraint("CK_ScreeningSourceResults_DurationMs", "[DurationMs] >= 0");
                    table.CheckConstraint("CK_ScreeningSourceResults_ErrorCode", "[ErrorCode] IS NULL OR [ErrorCode] IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_ScreeningSourceResults_MatchThreshold", "[MatchThreshold] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ScreeningSourceResults_Outcome", "([Status] = 0 AND [ErrorCode] IS NULL AND [ErrorMessage] IS NULL) OR ([Status] = 1 AND [ErrorCode] IN (0, 1) AND [ErrorMessage] IS NOT NULL) OR ([Status] = 2 AND [ErrorCode] = 2 AND [ErrorMessage] IS NOT NULL) OR ([Status] = 3 AND [ErrorCode] = 3 AND [ErrorMessage] IS NOT NULL)");
                    table.CheckConstraint("CK_ScreeningSourceResults_Source", "[Source] IN (0, 1, 2)");
                    table.CheckConstraint("CK_ScreeningSourceResults_Status", "[Status] IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_ScreeningSourceResults_UnsuccessfulCounts", "[Status] = 0 OR ([Hits] = 0 AND [ReturnedResults] = 0)");
                    table.ForeignKey(
                        name: "FK_ScreeningSourceResults_ScreeningRuns_RunId",
                        column: x => x.RunId,
                        principalSchema: "screening",
                        principalTable: "ScreeningRuns",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningMatches",
                schema: "screening",
                columns: table => new
                {
                    ScreeningMatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScreeningSourceResultId = table.Column<long>(type: "bigint", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ReferenceId = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    OverallScore = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    TokenSimilarity = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    EditSimilarity = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    IsExactMatch = table.Column<bool>(type: "bit", nullable: false),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningMatches", x => x.ScreeningMatchId);
                    table.CheckConstraint("CK_ScreeningMatches_ExactScore", "[IsExactMatch] = 0 OR [OverallScore] = 100");
                    table.CheckConstraint("CK_ScreeningMatches_FieldsJson", "ISJSON([FieldsJson]) = 1 AND DATALENGTH([FieldsJson]) <= 131072");
                    table.CheckConstraint("CK_ScreeningMatches_Scores", "[OverallScore] BETWEEN 0 AND 100 AND [TokenSimilarity] BETWEEN 0 AND 100 AND [EditSimilarity] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ScreeningMatches_SortOrder", "[SortOrder] >= 0");
                    table.ForeignKey(
                        name: "FK_ScreeningMatches_ScreeningSourceResults_ScreeningSourceResultId",
                        column: x => x.ScreeningSourceResultId,
                        principalSchema: "screening",
                        principalTable: "ScreeningSourceResults",
                        principalColumn: "ScreeningSourceResultId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningMatches_ScreeningSourceResultId_SortOrder",
                schema: "screening",
                table: "ScreeningMatches",
                columns: new[] { "ScreeningSourceResultId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningRuns_RequestedAtUtc",
                schema: "screening",
                table: "ScreeningRuns",
                column: "RequestedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningRuns_UserId_RequestedAtUtc",
                schema: "screening",
                table: "ScreeningRuns",
                columns: new[] { "UserId", "RequestedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningSourceResults_RunId_Source",
                schema: "screening",
                table: "ScreeningSourceResults",
                columns: new[] { "RunId", "Source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScreeningMatches",
                schema: "screening");

            migrationBuilder.DropTable(
                name: "ScreeningSourceResults",
                schema: "screening");

            migrationBuilder.DropTable(
                name: "ScreeningRuns",
                schema: "screening");
        }
    }
}
