using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnythingCanBeFarming.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommonNameCompactName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompactName",
                schema: "reference",
                table: "wikidata_common_name",
                type: "text",
                nullable: false,
                computedColumnSql: "replace(replace(\"NormalizedName\", ' ', ''), '-', '')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_wikidata_common_name_CompactName_trgm",
                schema: "reference",
                table: "wikidata_common_name",
                column: "CompactName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wikidata_common_name_CompactName_trgm",
                schema: "reference",
                table: "wikidata_common_name");

            migrationBuilder.DropColumn(
                name: "CompactName",
                schema: "reference",
                table: "wikidata_common_name");
        }
    }
}
