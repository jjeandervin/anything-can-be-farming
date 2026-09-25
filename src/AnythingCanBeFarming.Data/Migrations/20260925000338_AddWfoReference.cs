using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnythingCanBeFarming.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWfoReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reference");

            migrationBuilder.CreateTable(
                name: "wfo_import",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DatasetVersion = table.Column<string>(type: "text", nullable: true),
                    SourceFileName = table.Column<string>(type: "text", nullable: false),
                    SourceFileHash = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RowsRead = table.Column<long>(type: "bigint", nullable: false),
                    RowsImported = table.Column<long>(type: "bigint", nullable: false),
                    RowsRejected = table.Column<long>(type: "bigint", nullable: false),
                    WarningCount = table.Column<long>(type: "bigint", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ValidationJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wfo_import", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wfo_taxon",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaxonId = table.Column<string>(type: "text", nullable: false),
                    ScientificNameId = table.Column<string>(type: "text", nullable: true),
                    LocalId = table.Column<string>(type: "text", nullable: true),
                    ScientificName = table.Column<string>(type: "text", nullable: false),
                    TaxonRank = table.Column<string>(type: "text", nullable: true),
                    ParentNameUsageId = table.Column<string>(type: "text", nullable: true),
                    ScientificNameAuthorship = table.Column<string>(type: "text", nullable: true),
                    Family = table.Column<string>(type: "text", nullable: true),
                    Subfamily = table.Column<string>(type: "text", nullable: true),
                    Tribe = table.Column<string>(type: "text", nullable: true),
                    Subtribe = table.Column<string>(type: "text", nullable: true),
                    Genus = table.Column<string>(type: "text", nullable: true),
                    Subgenus = table.Column<string>(type: "text", nullable: true),
                    SpecificEpithet = table.Column<string>(type: "text", nullable: true),
                    InfraspecificEpithet = table.Column<string>(type: "text", nullable: true),
                    VerbatimTaxonRank = table.Column<string>(type: "text", nullable: true),
                    NomenclaturalStatus = table.Column<string>(type: "text", nullable: true),
                    NamePublishedIn = table.Column<string>(type: "text", nullable: true),
                    TaxonomicStatus = table.Column<string>(type: "text", nullable: true),
                    AcceptedNameUsageId = table.Column<string>(type: "text", nullable: true),
                    OriginalNameUsageId = table.Column<string>(type: "text", nullable: true),
                    NameAccordingToId = table.Column<string>(type: "text", nullable: true),
                    TaxonRemarks = table.Column<string>(type: "text", nullable: true),
                    WfoCreatedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    WfoModifiedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    References = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: true),
                    MajorGroup = table.Column<string>(type: "text", nullable: true),
                    TplId = table.Column<string>(type: "text", nullable: true),
                    ImportId = table.Column<long>(type: "bigint", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    AcceptedTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    OriginalTaxonId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wfo_taxon", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wfo_taxon_wfo_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "wfo_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_taxon_wfo_taxon_AcceptedTaxonId",
                        column: x => x.AcceptedTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_taxon_wfo_taxon_OriginalTaxonId",
                        column: x => x.OriginalTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_taxon_wfo_taxon_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wfo_import_SourceFileHash",
                schema: "reference",
                table: "wfo_import",
                column: "SourceFileHash");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_AcceptedNameUsageId",
                schema: "reference",
                table: "wfo_taxon",
                column: "AcceptedNameUsageId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_AcceptedTaxonId",
                schema: "reference",
                table: "wfo_taxon",
                column: "AcceptedTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_Family",
                schema: "reference",
                table: "wfo_taxon",
                column: "Family");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_Genus",
                schema: "reference",
                table: "wfo_taxon",
                column: "Genus");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_ImportId",
                schema: "reference",
                table: "wfo_taxon",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_IsCurrent",
                schema: "reference",
                table: "wfo_taxon",
                column: "IsCurrent");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_OriginalTaxonId",
                schema: "reference",
                table: "wfo_taxon",
                column: "OriginalTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_ParentId",
                schema: "reference",
                table: "wfo_taxon",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_ParentNameUsageId",
                schema: "reference",
                table: "wfo_taxon",
                column: "ParentNameUsageId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_ScientificName",
                schema: "reference",
                table: "wfo_taxon",
                column: "ScientificName");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_ScientificNameId",
                schema: "reference",
                table: "wfo_taxon",
                column: "ScientificNameId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_SpecificEpithet",
                schema: "reference",
                table: "wfo_taxon",
                column: "SpecificEpithet");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_TaxonId",
                schema: "reference",
                table: "wfo_taxon",
                column: "TaxonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_TaxonomicStatus",
                schema: "reference",
                table: "wfo_taxon",
                column: "TaxonomicStatus");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_TaxonRank",
                schema: "reference",
                table: "wfo_taxon",
                column: "TaxonRank");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_TplId",
                schema: "reference",
                table: "wfo_taxon",
                column: "TplId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wfo_taxon",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wfo_import",
                schema: "reference");
        }
    }
}
