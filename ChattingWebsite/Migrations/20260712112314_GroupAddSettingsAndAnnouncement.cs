using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChattingWebsite.Migrations
{
    /// <inheritdoc />
    public partial class GroupAddSettingsAndAnnouncement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowMemberEditName",
                table: "Groups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Announcement",
                table: "Groups",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncementUpdatedAt",
                table: "Groups",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowMemberEditName",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "Announcement",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "AnnouncementUpdatedAt",
                table: "Groups");
        }
    }
}
