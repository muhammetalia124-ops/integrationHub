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

[DbContext(typeof(CurrencyRateDbContext))]
[Migration("20260101000200_InitialCache")]
public partial class InitialCache : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "Cache");

        migrationBuilder.CreateTable(
            name: "CurrencyRates",
            schema: "Cache",
            columns: table => new
            {
                Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ForexBuying = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                ForexSelling = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CurrencyRates", x => x.Code));

        migrationBuilder.CreateTable(
            name: "PrayerTimeCache",
            schema: "Cache",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                DistrictId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                City = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                District = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrayerDate = table.Column<DateOnly>(type: "date", nullable: false),
                Imsak = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                Gunes = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                Ogle = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                Ikindi = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                Aksam = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                Yatsi = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_PrayerTimeCache", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ExternalApiCache",
            schema: "Cache",
            columns: table => new
            {
                CacheKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ExternalApiCache", x => x.CacheKey));

        migrationBuilder.CreateTable(
            name: "SharedApiData",
            schema: "Cache",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                DataType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RecordKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                DataDate = table.Column<DateOnly>(type: "date", nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IsAdminManaged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
            },
            constraints: table => table.PrimaryKey("PK_SharedApiData", x => x.Id));

        migrationBuilder.CreateIndex(
            "IX_PrayerTimeCache_District_Date", "PrayerTimeCache",
            new[] { "DistrictId", "PrayerDate" }, "Cache", unique: true);
        migrationBuilder.CreateIndex("IX_ExternalApiCache_ExpiresAtUtc", "ExternalApiCache", "ExpiresAtUtc", "Cache");
        migrationBuilder.CreateIndex(
            "IX_SharedApiData_Type_Key_Date", "SharedApiData",
            new[] { "DataType", "RecordKey", "DataDate" }, "Cache", unique: true);
        migrationBuilder.CreateIndex(
            "IX_SharedApiData_Type_Date", "SharedApiData",
            new[] { "DataType", "DataDate" }, "Cache");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SharedApiData", "Cache");
        migrationBuilder.DropTable("ExternalApiCache", "Cache");
        migrationBuilder.DropTable("PrayerTimeCache", "Cache");
        migrationBuilder.DropTable("CurrencyRates", "Cache");
    }
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
