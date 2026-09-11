using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChattingWebsite.Migrations
{
    /// <inheritdoc />
    public partial class GroupAddHistoryCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HistoryMessageCount",
                table: "Groups",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HistoryMessageCount",
                table: "Groups");
        }
    }
}
