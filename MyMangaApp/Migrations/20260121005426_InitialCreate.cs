using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyMangaApp.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Mangas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<long>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: true),
                    Author = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastUpdate = table.Column<long>(type: "INTEGER", nullable: false),
                    NextUpdate = table.Column<long>(type: "INTEGER", nullable: false),
                    Initialized = table.Column<bool>(type: "INTEGER", nullable: false),
                    ViewerFlags = table.Column<long>(type: "INTEGER", nullable: false),
                    ChapterFlags = table.Column<long>(type: "INTEGER", nullable: false),
                    CoverLastModified = table.Column<long>(type: "INTEGER", nullable: false),
                    DateAdded = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mangas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Chapters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MangaId = table.Column<long>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterNumber = table.Column<float>(type: "REAL", nullable: false),
                    DateUpload = table.Column<long>(type: "INTEGER", nullable: false),
                    DateFetch = table.Column<long>(type: "INTEGER", nullable: false),
                    Read = table.Column<bool>(type: "INTEGER", nullable: false),
                    Bookmark = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastPageRead = table.Column<long>(type: "INTEGER", nullable: false),
                    Scanlator = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chapters_Mangas_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Mangas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "History",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<long>(type: "INTEGER", nullable: false),
                    MangaId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRead = table.Column<long>(type: "INTEGER", nullable: false),
                    TimeRead = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                    table.ForeignKey(
                        name: "FK_History_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_History_Mangas_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Mangas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_MangaId",
                table: "Chapters",
                column: "MangaId");

            migrationBuilder.CreateIndex(
                name: "IX_History_ChapterId",
                table: "History",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_History_MangaId",
                table: "History",
                column: "MangaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "History");

            migrationBuilder.DropTable(
                name: "Chapters");

            migrationBuilder.DropTable(
                name: "Mangas");
        }
    }
}
