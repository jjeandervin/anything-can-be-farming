using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnythingCanBeFarming.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWikipediaReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wikipedia_article",
                schema: "reference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Wiki = table.Column<string>(type: "text", nullable: false),
                    RequestedTitle = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    PageId = table.Column<long>(type: "bigint", nullable: true),
                    LastRevId = table.Column<long>(type: "bigint", nullable: true),
                    WikibaseItem = table.Column<string>(type: "text", nullable: true),
                    ShortDescription = table.Column<string>(type: "text", nullable: true),
                    LeadText = table.Column<string>(type: "text", nullable: true),
                    LeadChars = table.Column<int>(type: "integer", nullable: true, computedColumnSql: "char_length(\"LeadText\")", stored: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    License = table.Column<string>(type: "text", nullable: false),
                    LicenseUrl = table.Column<string>(type: "text", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    ImportId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikipedia_article", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wikipedia_article_source_import_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "reference",
                        principalTable: "source_import",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wikipedia_item_article",
                schema: "reference",
                columns: table => new
                {
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    ArticleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikipedia_item_article", x => new { x.ItemId, x.ArticleId });
                    table.ForeignKey(
                        name: "FK_wikipedia_item_article_wikidata_item_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "reference",
                        principalTable: "wikidata_item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wikipedia_item_article_wikipedia_article_ArticleId",
                        column: x => x.ArticleId,
                        principalSchema: "reference",
                        principalTable: "wikipedia_article",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wikipedia_article_ImportId",
                schema: "reference",
                table: "wikipedia_article",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_wikipedia_article_Status",
                schema: "reference",
                table: "wikipedia_article",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_wikipedia_article_Wiki_RequestedTitle",
                schema: "reference",
                table: "wikipedia_article",
                columns: new[] { "Wiki", "RequestedTitle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wikipedia_article_WikibaseItem",
                schema: "reference",
                table: "wikipedia_article",
                column: "WikibaseItem");

            migrationBuilder.CreateIndex(
                name: "IX_wikipedia_item_article_ArticleId",
                schema: "reference",
                table: "wikipedia_item_article",
                column: "ArticleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wikipedia_item_article",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "wikipedia_article",
                schema: "reference");
        }
    }
}
