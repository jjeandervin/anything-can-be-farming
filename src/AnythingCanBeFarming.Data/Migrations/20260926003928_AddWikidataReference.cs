using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnythingCanBeFarming.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWikidataReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "source_import",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: true),
                    RowsRead = table.Column<long>(type: "bigint", nullable: false),
                    RowsInserted = table.Column<long>(type: "bigint", nullable: false),
                    RowsUpdated = table.Column<long>(type: "bigint", nullable: false),
                    RowsRetired = table.Column<long>(type: "bigint", nullable: false),
                    WarningCount = table.Column<long>(type: "bigint", nullable: false),
                    ValidationJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_import", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wikidata_item",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Qid = table.Column<string>(type: "text", nullable: false),
                    TaxonName = table.Column<string>(type: "text", nullable: true),
                    TaxonRankQid = table.Column<string>(type: "text", nullable: true),
                    LabelEn = table.Column<string>(type: "text", nullable: true),
                    EnwikiTitle = table.Column<string>(type: "text", nullable: true),
                    ImageFile = table.Column<string>(type: "text", nullable: true),
                    LastRevId = table.Column<long>(type: "bigint", nullable: true),
                    DetailsFetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    CrosswalkImportId = table.Column<long>(type: "bigint", nullable: true),
                    DetailsImportId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikidata_item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wikidata_item_source_import_CrosswalkImportId",
                        column: x => x.CrosswalkImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wikidata_item_source_import_DetailsImportId",
                        column: x => x.DetailsImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wikidata_common_name",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NormalizedName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikidata_common_name", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wikidata_common_name_wikidata_item_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "reference",
                        principalTable: "wikidata_item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wikidata_external_id",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Property = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikidata_external_id", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wikidata_external_id_wikidata_item_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "reference",
                        principalTable: "wikidata_item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wikidata_wfo_link",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Qid = table.Column<string>(type: "text", nullable: false),
                    WfoId = table.Column<string>(type: "text", nullable: false),
                    StatementRank = table.Column<string>(type: "text", nullable: false),
                    ResolvedWfoId = table.Column<string>(type: "text", nullable: true),
                    WfoTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    AcceptedWfoTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    ResolutionStatus = table.Column<string>(type: "text", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikidata_wfo_link", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wikidata_wfo_link_source_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wikidata_wfo_link_wfo_taxon_AcceptedWfoTaxonId",
                        column: x => x.AcceptedWfoTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wikidata_wfo_link_wfo_taxon_WfoTaxonId",
                        column: x => x.WfoTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wikidata_wfo_link_wikidata_item_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "reference",
                        principalTable: "wikidata_item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_Genus_trgm",
                schema: "reference",
                table: "wfo_taxon",
                column: "Genus")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_wfo_taxon_ScientificName_trgm",
                schema: "reference",
                table: "wfo_taxon",
                column: "ScientificName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_source_import_Source_Kind_Status",
                schema: "reference",
                table: "source_import",
                columns: new[] { "Source", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_common_name_ItemId_Language_Name",
                schema: "reference",
                table: "wikidata_common_name",
                columns: new[] { "ItemId", "Language", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_common_name_NormalizedName_trgm",
                schema: "reference",
                table: "wikidata_common_name",
                column: "NormalizedName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_external_id_ItemId_Property_Value",
                schema: "reference",
                table: "wikidata_external_id",
                columns: new[] { "ItemId", "Property", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_external_id_Property_Value",
                schema: "reference",
                table: "wikidata_external_id",
                columns: new[] { "Property", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_item_CrosswalkImportId",
                schema: "reference",
                table: "wikidata_item",
                column: "CrosswalkImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_item_DetailsImportId",
                schema: "reference",
                table: "wikidata_item",
                column: "DetailsImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_item_IsCurrent",
                schema: "reference",
                table: "wikidata_item",
                column: "IsCurrent");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_item_Qid",
                schema: "reference",
                table: "wikidata_item",
                column: "Qid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_wfo_link_AcceptedWfoTaxonId",
                schema: "reference",
                table: "wikidata_wfo_link",
                column: "AcceptedWfoTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_wfo_link_ImportId",
                schema: "reference",
                table: "wikidata_wfo_link",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_wfo_link_ItemId",
                schema: "reference",
                table: "wikidata_wfo_link",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_wfo_link_Qid_WfoId",
                schema: "reference",
                table: "wikidata_wfo_link",
                columns: new[] { "Qid", "WfoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_wfo_link_WfoId",
                schema: "reference",
                table: "wikidata_wfo_link",
                column: "WfoId");

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_wfo_link_WfoTaxonId",
                schema: "reference",
                table: "wikidata_wfo_link",
                column: "WfoTaxonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wikidata_common_name",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wikidata_external_id",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wikidata_wfo_link",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wikidata_item",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "source_import",
                schema: "reference");

            migrationBuilder.DropIndex(
                name: "IX_wfo_taxon_Genus_trgm",
                schema: "reference",
                table: "wfo_taxon");

            migrationBuilder.DropIndex(
                name: "IX_wfo_taxon_ScientificName_trgm",
                schema: "reference",
                table: "wfo_taxon");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
