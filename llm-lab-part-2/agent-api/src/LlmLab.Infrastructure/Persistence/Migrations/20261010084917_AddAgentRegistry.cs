using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentId",
                table: "Conversations",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "assistant");

            migrationBuilder.CreateTable(
                name: "AgentDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AllowedToolsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CanDelegate = table.Column<bool>(type: "bit", nullable: false),
                    Hosting = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitions");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Conversations");
        }
    }
}
