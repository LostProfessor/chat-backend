using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChattingWebsite.Migrations
{
    /// <inheritdoc />
    public partial class AddMuteAndRecall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMuted",
                table: "UserGroups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRecalled",
                table: "Messages",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsMuted",
                table: "UserGroups");

            migrationBuilder.DropColumn(
                name: "IsRecalled",
                table: "Messages");
        }
    }
}
