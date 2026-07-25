using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EyRiskScreening.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSuppliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CommercialName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxId = table.Column<string>(type: "varchar(11)", unicode: false, maxLength: 11, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Website = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    PhysicalAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AnnualBillingUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LastEditedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.CheckConstraint("CK_Suppliers_AnnualBillingUsd", "[AnnualBillingUsd] >= 0");
                    table.CheckConstraint("CK_Suppliers_TaxId", "DATALENGTH([TaxId]) = 11 AND [TaxId] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9]%'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_LastEditedAtUtc",
                table: "Suppliers",
                column: "LastEditedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_LegalName",
                table: "Suppliers",
                column: "LegalName");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TaxId",
                table: "Suppliers",
                column: "TaxId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Suppliers");
        }
    }
}
