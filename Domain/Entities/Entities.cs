using System.ComponentModel.DataAnnotations;

namespace IntegrationHub.Domain;

public class User
{
    public int Id { get; set; }

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; }
}

public class UserActivity
{
    public int Id { get; set; }
    public int UserId { get; set; }

    [Required]
    [MaxLength(64)]
    public string ServiceType { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string SubCategory { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? InputLabel { get; set; }

    [MaxLength(256)]
    public string? InputValue { get; set; }

    [MaxLength(128)]
    public string? ResultLabel { get; set; }

    [MaxLength(512)]
    public string? ResultValue { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class PrayerTimeCache
{
    public int Id { get; set; }

    [Required]
    [MaxLength(32)]
    public string DistrictId { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string City { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string District { get; set; } = string.Empty;

    public DateOnly PrayerDate { get; set; }

    [Required]
    [MaxLength(5)]
    public string Imsak { get; set; } = string.Empty;

    [Required]
    [MaxLength(5)]
    public string Gunes { get; set; } = string.Empty;

    [Required]
    [MaxLength(5)]
    public string Ogle { get; set; } = string.Empty;

    [Required]
    [MaxLength(5)]
    public string Ikindi { get; set; } = string.Empty;

    [Required]
    [MaxLength(5)]
    public string Aksam { get; set; } = string.Empty;

    [Required]
    [MaxLength(5)]
    public string Yatsi { get; set; } = string.Empty;

    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CurrencyRate
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal ForexBuying { get; set; }
    public decimal ForexSelling { get; set; }
    public DateTime FetchedAtUtc { get; set; }
}

public sealed class ExternalApiCacheEntry
{
    public string CacheKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime FetchedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class SharedApiDataRecord
{
    public int Id { get; set; }
    public string DataType { get; set; } = string.Empty;
    public string RecordKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateOnly DataDate { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime FetchedAtUtc { get; set; }
    public bool IsAdminManaged { get; set; }
    public bool IsDeleted { get; set; }
}
