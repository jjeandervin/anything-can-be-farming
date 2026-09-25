using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnythingCanBeFarming.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWfoSupplemental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DatasetKind",
                schema: "reference",
                table: "wfo_import",
                type: "text",
                nullable: false,
                defaultValue: "Backbone");

            migrationBuilder.CreateTable(
                name: "wfo_deduplicated_id",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeprecatedWfoId = table.Column<string>(type: "text", nullable: false),
                    ReplacementWfoId = table.Column<string>(type: "text", nullable: false),
                    CanonicalName = table.Column<string>(type: "text", nullable: true),
                    AuthorsString = table.Column<string>(type: "text", nullable: true),
                    Rank = table.Column<string>(type: "text", nullable: true),
                    NomenclaturalStatus = table.Column<string>(type: "text", nullable: true),
                    DeprecatedTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    ReplacementTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wfo_deduplicated_id", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wfo_deduplicated_id_wfo_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "wfo_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_deduplicated_id_wfo_taxon_DeprecatedTaxonId",
                        column: x => x.DeprecatedTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_deduplicated_id_wfo_taxon_ReplacementTaxonId",
                        column: x => x.ReplacementTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wfo_deprecated_name",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WfoId = table.Column<string>(type: "text", nullable: false),
                    CanonicalName = table.Column<string>(type: "text", nullable: true),
                    AuthorsString = table.Column<string>(type: "text", nullable: true),
                    Rank = table.Column<string>(type: "text", nullable: true),
                    NomenclaturalStatus = table.Column<string>(type: "text", nullable: true),
                    WfoTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wfo_deprecated_name", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wfo_deprecated_name_wfo_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "wfo_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_deprecated_name_wfo_taxon_WfoTaxonId",
                        column: x => x.WfoTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wfo_ipni_mapping",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpniId = table.Column<string>(type: "text", nullable: false),
                    NormalizedIpniId = table.Column<string>(type: "text", nullable: false),
                    WfoId = table.Column<string>(type: "text", nullable: false),
                    WfoTaxonId = table.Column<long>(type: "bigint", nullable: true),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wfo_ipni_mapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wfo_ipni_mapping_wfo_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "wfo_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wfo_ipni_mapping_wfo_taxon_WfoTaxonId",
                        column: x => x.WfoTaxonId,
                        principalSchema: "reference",
                        principalTable: "wfo_taxon",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deduplicated_id_DeprecatedTaxonId",
                schema: "reference",
                table: "wfo_deduplicated_id",
                column: "DeprecatedTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deduplicated_id_DeprecatedWfoId",
                schema: "reference",
                table: "wfo_deduplicated_id",
                column: "DeprecatedWfoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deduplicated_id_ImportId",
                schema: "reference",
                table: "wfo_deduplicated_id",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deduplicated_id_ReplacementTaxonId",
                schema: "reference",
                table: "wfo_deduplicated_id",
                column: "ReplacementTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deduplicated_id_ReplacementWfoId",
                schema: "reference",
                table: "wfo_deduplicated_id",
                column: "ReplacementWfoId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deprecated_name_CanonicalName",
                schema: "reference",
                table: "wfo_deprecated_name",
                column: "CanonicalName");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deprecated_name_ImportId",
                schema: "reference",
                table: "wfo_deprecated_name",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deprecated_name_Rank",
                schema: "reference",
                table: "wfo_deprecated_name",
                column: "Rank");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deprecated_name_WfoId",
                schema: "reference",
                table: "wfo_deprecated_name",
                column: "WfoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wfo_deprecated_name_WfoTaxonId",
                schema: "reference",
                table: "wfo_deprecated_name",
                column: "WfoTaxonId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_ipni_mapping_ImportId",
                schema: "reference",
                table: "wfo_ipni_mapping",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_ipni_mapping_IpniId",
                schema: "reference",
                table: "wfo_ipni_mapping",
                column: "IpniId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_ipni_mapping_IpniId_WfoId",
                schema: "reference",
                table: "wfo_ipni_mapping",
                columns: new[] { "IpniId", "WfoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wfo_ipni_mapping_NormalizedIpniId",
                schema: "reference",
                table: "wfo_ipni_mapping",
                column: "NormalizedIpniId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_ipni_mapping_WfoId",
                schema: "reference",
                table: "wfo_ipni_mapping",
                column: "WfoId");

            migrationBuilder.CreateIndex(
                name: "IX_wfo_ipni_mapping_WfoTaxonId",
                schema: "reference",
                table: "wfo_ipni_mapping",
                column: "WfoTaxonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wfo_deduplicated_id",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wfo_deprecated_name",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wfo_ipni_mapping",
                schema: "reference");

            migrationBuilder.DropColumn(
                name: "DatasetKind",
                schema: "reference",
                table: "wfo_import");
        }
    }
}
