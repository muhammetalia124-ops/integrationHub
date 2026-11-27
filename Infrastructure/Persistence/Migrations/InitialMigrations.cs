using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IntegrationHub.Infrastructure.Persistence;

[DbContext(typeof(UserDbContext))]
[Migration("20260101000000_InitialAuth")]
public partial class InitialAuth : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "Auth");
        migrationBuilder.CreateTable(
            name: "Users",
            schema: "Auth",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                IsAdmin = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Users", x => x.Id));
        migrationBuilder.CreateIndex("IX_Users_Email", "Users", "Email", "Auth", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("Users", "Auth");
}

[DbContext(typeof(UserActivityDbContext))]
[Migration("20260101000100_InitialActivity")]
public partial class InitialActivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "Activity");
        migrationBuilder.CreateTable(
            name: "UserActivities",
            schema: "Activity",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<int>(type: "integer", nullable: false),
                ServiceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SubCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                InputLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                InputValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ResultLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ResultValue = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_UserActivities", x => x.Id));
        migrationBuilder.CreateIndex(
            "IX_UserActivities_Lookup", "UserActivities",
            new[] { "UserId", "ServiceType", "SubCategory" }, "Activity");
        migrationBuilder.CreateIndex("IX_UserActivities_Timestamp", "UserActivities", "Timestamp", "Activity");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("UserActivities", "Activity");
}

[DbContext(typeof(UserDbContext))]
public partial class UserDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) { }
}

[DbContext(typeof(UserActivityDbContext))]
public partial class UserActivityDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) { }
}

[DbContext(typeof(CurrencyRateDbContext))]
public partial class CurrencyRateDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) { }
}
