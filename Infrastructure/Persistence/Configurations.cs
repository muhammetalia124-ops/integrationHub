using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure.Persistence;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", "Auth");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Email).IsRequired().HasMaxLength(256);
        builder.HasIndex(user => user.Email).IsUnique();
        builder.Property(user => user.Username).IsRequired().HasMaxLength(128);
        builder.Property(user => user.PasswordHash).IsRequired().HasMaxLength(255);
        builder.Property(user => user.CreatedAt).IsRequired();
        builder.Property(user => user.IsActive).IsRequired();
        builder.Property(user => user.IsAdmin).IsRequired();
    }
}

public class UserActivityConfiguration : IEntityTypeConfiguration<UserActivity>
{
    public void Configure(EntityTypeBuilder<UserActivity> builder)
    {
        builder.ToTable("UserActivities", "Activity");
        builder.HasKey(activity => activity.Id);
        builder.Property(activity => activity.UserId).IsRequired();
        builder.Property(activity => activity.ServiceType).IsRequired().HasMaxLength(64);
        builder.Property(activity => activity.SubCategory).IsRequired().HasMaxLength(64);
        builder.Property(activity => activity.InputLabel).HasMaxLength(128);
        builder.Property(activity => activity.InputValue).HasMaxLength(256);
        builder.Property(activity => activity.ResultLabel).HasMaxLength(128);
        builder.Property(activity => activity.ResultValue).HasMaxLength(512);
        builder.Property(activity => activity.Timestamp).IsRequired();
        builder.HasIndex(activity => new { activity.UserId, activity.ServiceType, activity.SubCategory })
            .HasDatabaseName("IX_UserActivities_Lookup");
        builder.HasIndex(activity => activity.Timestamp)
            .HasDatabaseName("IX_UserActivities_Timestamp");
    }
}

public class PrayerTimeCacheConfiguration : IEntityTypeConfiguration<PrayerTimeCache>
{
    public void Configure(EntityTypeBuilder<PrayerTimeCache> builder)
    {
        builder.ToTable("PrayerTimeCache", "Cache");
        builder.HasKey(prayer => prayer.Id);
        builder.Property(prayer => prayer.DistrictId).IsRequired().HasMaxLength(32);
        builder.Property(prayer => prayer.City).IsRequired().HasMaxLength(128);
        builder.Property(prayer => prayer.District).IsRequired().HasMaxLength(128);
        builder.Property(prayer => prayer.PrayerDate).IsRequired().HasColumnType("date");
        builder.Property(prayer => prayer.Imsak).IsRequired().HasMaxLength(5);
        builder.Property(prayer => prayer.Gunes).IsRequired().HasMaxLength(5);
        builder.Property(prayer => prayer.Ogle).IsRequired().HasMaxLength(5);
        builder.Property(prayer => prayer.Ikindi).IsRequired().HasMaxLength(5);
        builder.Property(prayer => prayer.Aksam).IsRequired().HasMaxLength(5);
        builder.Property(prayer => prayer.Yatsi).IsRequired().HasMaxLength(5);
        builder.Property(prayer => prayer.FetchedAtUtc).IsRequired();
        builder.HasIndex(prayer => new { prayer.DistrictId, prayer.PrayerDate })
            .IsUnique()
            .HasDatabaseName("IX_PrayerTimeCache_District_Date");
    }
}

public sealed class CurrencyRateConfiguration : IEntityTypeConfiguration<CurrencyRate>
{
    public void Configure(EntityTypeBuilder<CurrencyRate> builder)
    {
        builder.ToTable("CurrencyRates", "Cache");
        builder.HasKey(rate => rate.Code);
        builder.Property(rate => rate.Code).HasMaxLength(3).IsRequired();
        builder.Property(rate => rate.Name).HasMaxLength(128).IsRequired();
        builder.Property(rate => rate.ForexBuying).HasPrecision(18, 8).IsRequired();
        builder.Property(rate => rate.ForexSelling).HasPrecision(18, 8).IsRequired();
        builder.Property(rate => rate.FetchedAtUtc).IsRequired();
    }
}

public sealed class ExternalApiCacheConfiguration : IEntityTypeConfiguration<ExternalApiCacheEntry>
{
    public void Configure(EntityTypeBuilder<ExternalApiCacheEntry> builder)
    {
        builder.ToTable("ExternalApiCache", "Cache");
        builder.HasKey(entry => entry.CacheKey);
        builder.Property(entry => entry.CacheKey).HasMaxLength(256).IsRequired();
        builder.Property(entry => entry.PayloadJson).IsRequired();
        builder.Property(entry => entry.FetchedAtUtc).IsRequired();
        builder.Property(entry => entry.ExpiresAtUtc).IsRequired();
        builder.HasIndex(entry => entry.ExpiresAtUtc).HasDatabaseName("IX_ExternalApiCache_ExpiresAtUtc");
    }
}

public sealed class SharedApiDataConfiguration : IEntityTypeConfiguration<SharedApiDataRecord>
{
    public void Configure(EntityTypeBuilder<SharedApiDataRecord> builder)
    {
        builder.ToTable("SharedApiData", "Cache");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.DataType).IsRequired().HasMaxLength(32);
        builder.Property(record => record.RecordKey).IsRequired().HasMaxLength(256);
        builder.Property(record => record.DisplayName).IsRequired().HasMaxLength(128);
        builder.Property(record => record.DataDate).IsRequired().HasColumnType("date");
        builder.Property(record => record.PayloadJson).IsRequired();
        builder.Property(record => record.FetchedAtUtc).IsRequired();
        builder.Property(record => record.IsAdminManaged).IsRequired().HasDefaultValue(false);
        builder.Property(record => record.IsDeleted).IsRequired().HasDefaultValue(false);
        builder.HasIndex(record => new { record.DataType, record.RecordKey, record.DataDate })
            .IsUnique()
            .HasDatabaseName("IX_SharedApiData_Type_Key_Date");
        builder.HasIndex(record => new { record.DataType, record.DataDate })
            .HasDatabaseName("IX_SharedApiData_Type_Date");
    }
}
