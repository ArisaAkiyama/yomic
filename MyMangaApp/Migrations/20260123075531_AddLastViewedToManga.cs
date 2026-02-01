using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyMangaApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLastViewedToManga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastViewed",
                table: "Mangas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsDownloaded",
                table: "Chapters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastViewed",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "IsDownloaded",
                table: "Chapters");
        }
    }
}
