using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yomic.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryUpdateExcluded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UpdateExcluded",
                table: "Categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateExcluded",
                table: "Categories");
        }
    }
}
