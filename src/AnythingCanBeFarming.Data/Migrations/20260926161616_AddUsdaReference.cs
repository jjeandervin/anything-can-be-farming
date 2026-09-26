using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnythingCanBeFarming.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsdaReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "place_label",
                schema: "reference",
                columns: table => new
                {
                    Scheme = table.Column<string>(type: "text", nullable: false),
                    PlaceId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    CountryCode = table.Column<string>(type: "text", nullable: true),
                    AdminCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_place_label", x => new { x.Scheme, x.PlaceId });
                });

            migrationBuilder.CreateTable(
                name: "usda_code_label",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    TypeUri = table.Column<string>(type: "text", nullable: true),
                    Label = table.Column<string>(type: "text", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: true),
                    Confidence = table.Column<string>(type: "text", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_code_label", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usda_remark",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_remark", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usda_taxon",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    ScientificName = table.Column<string>(type: "text", nullable: false),
                    CanonicalName = table.Column<string>(type: "text", nullable: true),
                    FamilyUsda = table.Column<string>(type: "text", nullable: true),
                    TaxonRank = table.Column<string>(type: "text", nullable: true),
                    TaxonomicStatus = table.Column<string>(type: "text", nullable: true),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_taxon", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usda_taxon_source_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usda_trait_type",
                schema: "reference",
                columns: table => new
                {
                    TypeUri = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    ValueKind = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_trait_type", x => x.TypeUri);
                });

            migrationBuilder.CreateTable(
                name: "usda_distribution",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UsdaTaxonId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    PlaceRaw = table.Column<string>(type: "text", nullable: false),
                    PlaceScheme = table.Column<string>(type: "text", nullable: false),
                    PlaceId = table.Column<string>(type: "text", nullable: true),
                    RemarkId = table.Column<long>(type: "bigint", nullable: true),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_distribution", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usda_distribution_source_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_distribution_usda_remark_RemarkId",
                        column: x => x.RemarkId,
                        principalSchema: "reference",
                        principalTable: "usda_remark",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_distribution_usda_taxon_UsdaTaxonId",
                        column: x => x.UsdaTaxonId,
                        principalSchema: "reference",
                        principalTable: "usda_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usda_fact",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UsdaTaxonId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    OccurrenceId = table.Column<string>(type: "text", nullable: false),
                    TypeUri = table.Column<string>(type: "text", nullable: false),
                    ValueRaw = table.Column<string>(type: "text", nullable: false),
                    ValueCode = table.Column<string>(type: "text", nullable: false, computedColumnSql: "CASE WHEN \"ValueRaw\" ~ '^https?://' THEN regexp_replace(\"ValueRaw\", '^.*[/#]', '') ELSE \"ValueRaw\" END", stored: true),
                    ValueNumeric = table.Column<decimal>(type: "numeric", nullable: true),
                    UnitUri = table.Column<string>(type: "text", nullable: true),
                    BodyPartUri = table.Column<string>(type: "text", nullable: true),
                    LifeStageUri = table.Column<string>(type: "text", nullable: true),
                    StatisticalMethod = table.Column<string>(type: "text", nullable: true),
                    SourceTerm = table.Column<string>(type: "text", nullable: true),
                    RemarkId = table.Column<long>(type: "bigint", nullable: true),
                    MethodRemarkId = table.Column<long>(type: "bigint", nullable: true),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_fact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usda_fact_source_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_fact_usda_remark_MethodRemarkId",
                        column: x => x.MethodRemarkId,
                        principalSchema: "reference",
                        principalTable: "usda_remark",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_fact_usda_remark_RemarkId",
                        column: x => x.RemarkId,
                        principalSchema: "reference",
                        principalTable: "usda_remark",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_fact_usda_taxon_UsdaTaxonId",
                        column: x => x.UsdaTaxonId,
                        principalSchema: "reference",
                        principalTable: "usda_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usda_wfo_link",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UsdaTaxonId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    WfoTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    AcceptedWfoTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DetailJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usda_wfo_link", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usda_wfo_link_usda_taxon_UsdaTaxonId",
                        column: x => x.UsdaTaxonId,
                        principalSchema: "reference",
                        principalTable: "usda_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_wfo_link_wfo_taxon_AcceptedWfoTaxonId",
                        column: x => x.AcceptedWfoTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usda_wfo_link_wfo_taxon_WfoTaxonId",
                        column: x => x.WfoTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_place_label_CountryCode_AdminCode",
                schema: "reference",
                table: "place_label",
                columns: new[] { "CountryCode", "AdminCode" });

            migrationBuilder.CreateIndex(
                name: "IX_usda_code_label_Code_TypeUri",
                schema: "reference",
                table: "usda_code_label",
                columns: new[] { "Code", "TypeUri" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_usda_distribution_ImportId",
                schema: "reference",
                table: "usda_distribution",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_distribution_Kind_PlaceId",
                schema: "reference",
                table: "usda_distribution",
                columns: new[] { "Kind", "PlaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_usda_distribution_RemarkId",
                schema: "reference",
                table: "usda_distribution",
                column: "RemarkId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_distribution_Symbol",
                schema: "reference",
                table: "usda_distribution",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_usda_distribution_UsdaTaxonId_Kind_PlaceRaw",
                schema: "reference",
                table: "usda_distribution",
                columns: new[] { "UsdaTaxonId", "Kind", "PlaceRaw" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_ImportId",
                schema: "reference",
                table: "usda_fact",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_MethodRemarkId",
                schema: "reference",
                table: "usda_fact",
                column: "MethodRemarkId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_RemarkId",
                schema: "reference",
                table: "usda_fact",
                column: "RemarkId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_Symbol",
                schema: "reference",
                table: "usda_fact",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_TypeUri",
                schema: "reference",
                table: "usda_fact",
                column: "TypeUri");

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_TypeUri_ValueRaw",
                schema: "reference",
                table: "usda_fact",
                columns: new[] { "TypeUri", "ValueRaw" });

            migrationBuilder.CreateIndex(
                name: "IX_usda_fact_UsdaTaxonId",
                schema: "reference",
                table: "usda_fact",
                column: "UsdaTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_remark_Sha256",
                schema: "reference",
                table: "usda_remark",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usda_taxon_CanonicalName",
                schema: "reference",
                table: "usda_taxon",
                column: "CanonicalName");

            migrationBuilder.CreateIndex(
                name: "IX_usda_taxon_ImportId",
                schema: "reference",
                table: "usda_taxon",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_taxon_Symbol",
                schema: "reference",
                table: "usda_taxon",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usda_trait_type_Key",
                schema: "reference",
                table: "usda_trait_type",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usda_wfo_link_AcceptedWfoTaxonId",
                schema: "reference",
                table: "usda_wfo_link",
                column: "AcceptedWfoTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_usda_wfo_link_Symbol",
                schema: "reference",
                table: "usda_wfo_link",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_usda_wfo_link_UsdaTaxonId",
                schema: "reference",
                table: "usda_wfo_link",
                column: "UsdaTaxonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usda_wfo_link_WfoTaxonId",
                schema: "reference",
                table: "usda_wfo_link",
                column: "WfoTaxonId");

            // For inspection only. A trait-specific label row overrides the generic row for its code even when
            // its label is null, so an unresolved override (shade tolerance) never falls back to a generic label.
            migrationBuilder.Sql("""
                CREATE VIEW reference.usda_fact_labeled AS
                SELECT f."Id" AS fact_id, f."UsdaTaxonId" AS usda_taxon_id, f."Symbol" AS symbol,
                    tx."ScientificName" AS scientific_name, f."TypeUri" AS type_uri, t."Key" AS trait_key,
                    t."Label" AS trait_label, t."ValueKind" AS value_kind, f."ValueRaw" AS value_raw,
                    f."ValueCode" AS value_code, f."ValueNumeric" AS value_numeric, f."UnitUri" AS unit_uri,
                    f."StatisticalMethod" AS statistical_method,
                    CASE WHEN s."Id" IS NOT NULL THEN s."Label" ELSE g."Label" END AS value_label,
                    CASE WHEN s."Id" IS NOT NULL THEN s."Ordinal" ELSE g."Ordinal" END AS value_ordinal,
                    CASE WHEN s."Id" IS NOT NULL THEN s."Confidence" ELSE g."Confidence" END AS label_confidence,
                    s."TypeUri" AS label_type_uri,
                    CASE WHEN f."ValueNumeric" IS NOT NULL AND t."ValueKind" = 'numeric' THEN f."ValueRaw"
                         WHEN s."Id" IS NOT NULL AND s."Label" IS NULL THEN f."ValueCode" || ' (' || s."Confidence" || ')'
                         WHEN s."Id" IS NOT NULL THEN s."Label"
                         WHEN g."Id" IS NOT NULL AND g."Label" IS NOT NULL THEN g."Label"
                         ELSE f."ValueCode" || ' (unmapped)' END AS value_display,
                    f."BodyPartUri" AS body_part_uri, f."LifeStageUri" AS life_stage_uri, f."SourceTerm" AS source_term,
                    m."Text" AS method_text
                FROM reference.usda_fact f
                JOIN reference.usda_taxon tx ON tx."Id" = f."UsdaTaxonId"
                LEFT JOIN reference.usda_trait_type t ON t."TypeUri" = f."TypeUri"
                LEFT JOIN reference.usda_code_label s ON s."Code" = f."ValueCode" AND s."TypeUri" = f."TypeUri"
                LEFT JOIN reference.usda_code_label g ON g."Code" = f."ValueCode" AND g."TypeUri" IS NULL
                LEFT JOIN reference.usda_remark m ON m."Id" = f."MethodRemarkId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW reference.usda_fact_labeled;");

            migrationBuilder.DropTable(
                name: "place_label",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_code_label",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_distribution",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_fact",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_trait_type",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_wfo_link",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_remark",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "usda_taxon",
                schema: "reference");
        }
    }
}
