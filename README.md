# IntegrationHub

**IntegrationHub**, farklı dış kaynaklardan (döviz kurları, altın fiyatları, hava durumu, namaz vakitleri ve ters coğrafi kodlama) alınan verileri tek bir merkezde toplayan, çok katmanlı önbellekleme (cache), yedek kaynak (fallback) mekanizması, yönetici veri yönetimi ve kullanıcı işlem geçmişi sunan **.NET 8** tabanlı bir web uygulamasıdır.

---

## Temel Özellikler

* **Modüler Entegrasyon Mimarisi:** `IIntegrationService` arayüzünü uygulayan tüm servisler reflection ile otomatik olarak sisteme kaydedilir ve dinamik form metadata'sı üretir.
* **Çok Kaynaklı Veri ve Yedekleme (Fallback):**
  * **Döviz:** TCMB üzerinden günlük kurları çeker ve TL karşılığı dönüşüm yapar.
  * **Altın:** Birincil kaynak olarak Truncgil Finans verilerini kullanır. Erişim sorunu yaşanırsa uluslararası ons altın fiyatı ve TCMB dolar kuru üzerinden otomatik hesaplama yapan yedek sisteme geçer.
  * **Hava Durumu:** Open-Meteo Geocoding ve Forecast servislerini kullanır. Yanıt alınamazsa wttr.in yedek servisine geçer.
  * **Namaz Vakitleri:** Diyanet verileri üzerinden il ve ilçe bazında günlük vakitleri sunar.
  * **Konum Çözümleme:** Tarayıcı koordinatlarını OpenStreetMap Nominatim üzerinden il ve ilçe bilgisine dönüştürür.
* **Çok Katmanlı Cache ve Günlük Arşiv (`SharedApiData`):**
  * Dış API çağrılarını azaltmak ve eşzamanlı isteklerde (cache stampede) dış servisleri korumak için `SemaphoreSlim` kilitli veritabanı önbelleği kullanır.
  * Çekilen günlük döviz, altın, hava durumu ve namaz vakti verileri otomatik olarak ortak veri arşivine kaydedilir.
* **Yönetici Veri Ezme (Admin Override):** Yöneticiler ortak veri arşivindeki kayıtları ekleyebilir, güncelleyebilir veya silebilir. Yönetici tarafından düzenlenen günlük kayıtlar dış API güncellemeleriyle ezilmez ve o günkü hesaplamalarda öncelikli kullanılır.
* **Kimlik Doğrulama ve Yetkilendirme:**
  * BCrypt (`workFactor: 11`) ile şifreleme ve JWT tabanlı oturum yönetimi.
  * Yönetici işlemleri için veritabanından anlık durum kontrolü yapan özel `AdminAuthorizationFilter`.
* **Idempotent İşlem Geçmişi:** Kullanıcıların yaptığı hesaplamalar tekrarlı kayıt oluşturmayacak şekilde kişisel geçmişe kaydedilir ve istenildiğinde temizlenebilir.

---

## Mimari Yapı

Proje sorumlulukların ayrılması (Separation of Concerns) ilkesine göre 3 ana katmandan oluşur:

```text
IntegrationHub/
├── Controllers/              # İnce (Thin) API uç noktaları (Auth, Admin, Search)
├── Middlewares/              # JWT doğrulama, Admin yetki filtresi ve global hata yönetimi
├── Domain/                   # Çekirdek katman (framework/EF Core bağımlılığı yok)
│   ├── Entities/             # Veritabanı varlık modelleri
│   ├── DTOs/                 # İstek/yanıt modelleri ve özel doğrulama kuralları
│   ├── Contracts/            # Servis, repository, cache ve güvenlik arayüzleri
│   └── Services/             # İş kuralları (AuthService, AdminService, SearchService, SharedApiDataService, UserActivityService)
├── Infrastructure/           # Dış dünya entegrasyonları ve teknik altyapı
│   ├── Persistence/          # DbContext, entity configuration, migration, repository, DatabaseInitializer ve MigrationState
│   │   ├── Migrations/       # InitialAuth, InitialActivity, InitialCache
│   │   └── Repositories/     # UserRepository, UserActivityRepository (IUserRepository / IUserActivityRepository)
│   ├── Integrations/         # TCMB, Altın, Hava Durumu, Namaz Vakitleri ve Konum servisleri
│   ├── Security/             # BCrypt şifreleme ve JWT üretimi
│   └── DependencyInjection.cs # DbContext, repository, cache store, HttpClient ve servis kayıtları (AddInfrastructure)
└── wwwroot/                  # Vanilla JS ve Tailwind CSS tabanlı tek sayfa arayüz (SPA)
```

### Katman Bağımlılık Yönü

```text
Web (Program.cs, Controllers, Middlewares)
   └── Infrastructure  →  Domain
```

* **Domain** hiçbir katmana bağımlı değildir; içinde EF Core/Npgsql paketi bulunmaz. Yalnızca entity, DTO, arayüz (Contracts) ve iş kurallarını (Services) barındırır.
* **Infrastructure**, Domain'i referans alır ve tüm EF Core/DbContext, repository, cache store ve veritabanı başlatma sorumluluğunu üstlenir.
* **Web**, servisleri yalnızca `AddInfrastructure(builder.Configuration)` çağrısı ile kaydeder; bu metot içinde önce persistence (`AddPersistence`), ardından iş kuralları (`AddDomain`) kaydedilir.

### Servis → Repository İlişkisi

`AuthService`, `AdminService` ve `UserActivityService` DbContext'lere doğrudan bağımlı değildir; veri erişimi `IUserRepository` ve `IUserActivityRepository` arayüzleri üzerinden yapılır. `AdminAuthorizationFilter` yönetici kontrolünü `AuthService.IsActiveAdminAsync` ile gerçekleştirir.

### Veritabanı Şema Tasarımı (PostgreSQL)

Uygulama PostgreSQL üzerinde mantıksal olarak ayrılmış 3 şema kullanır:

| Şema | DbContext | Tablolar | Açıklama |
| :--- | :--- | :--- | :--- |
| **`Auth`** | `UserDbContext` | `Users` | Kullanıcı hesapları, roller ve aktiflik durumları |
| **`Activity`** | `UserActivityDbContext` | `UserActivities` | Kullanıcıların kişisel hesaplama ve sorgu geçmişi |
| **`Cache`** | `CurrencyRateDbContext` | `CurrencyRates`, `PrayerTimeCache`, `ExternalApiCache`, `SharedApiData` | Dış API önbellekleri ve ortak günlük veri arşivi |

---

## Kurulum ve Çalıştırma

### Gereksinimler

* .NET 8 SDK
* PostgreSQL 14+ veya Docker

> `Domain` projesi saf C# olduğu için EF Core migration komutları artık Infrastructure katmanından çalıştırılır:
> `dotnet ef migrations add <Ad> --project Infrastructure --startup-project Infrastructure --context UserDbContext`

### Ortam Değişkenleri

Uygulamayı çalıştırmadan önce yerel ortam değişkenlerinizde veya git'e dahil edilmeyen yerel yapılandırma dosyanızda veritabanı bağlantısı, JWT ve ilk yönetici hesabı ayarlarının tanımlı olması gerekir.

### Uygulamayı Başlatma

Uygulama ayağa kalkarken `DatabaseInitializer` otomatik olarak veritabanı migration işlemlerini uygular ve sistemde yönetici yoksa ilk yönetici hesabını oluşturur.

```bash
dotnet restore
dotnet run
```

### Docker ile Çalıştırma

```bash
docker build -t integrationhub .
docker run -p 5080:8080 --env-file .env integrationhub
```

---

## API Uç Noktaları

### Kimlik Doğrulama (`/api/auth`)
* `POST /api/auth/register` — Yeni kullanıcı kaydı oluşturur.
* `POST /api/auth/login` — Giriş yapar ve JWT döndürür.
* `GET /api/auth/me` — Oturum açmış kullanıcının güncel bilgilerini getirir.

### Arama ve Hesaplama (`/api/search`)
* `GET /api/search/init` — Aktif entegrasyon kategorilerini ve dinamik form alanlarını döner.
* `POST /api/search/calculate` — Seçilen hizmete göre hesaplama yapar ve sonucu geçmişe işler.
* `POST /api/search/location` — Enlem/boylam bilgisinden il ve ilçe çözer.
* `GET /api/search/shared-data` — Ortak günlük veri arşivini tür ve tarih aralığına göre listeler.
* `GET /api/search/history` — Kullanıcının kendi işlem geçmişini listeler.
* `DELETE /api/search/history` — Kullanıcının kendi işlem geçmişini temizler.

### Yönetim Paneli (`/api/admin` — *Yalnızca Admin*)
* `GET / POST /api/admin/users` — Kullanıcıları listeler veya yeni kullanıcı ekler.
* `PUT / DELETE /api/admin/users/{id}` — Kullanıcı bilgilerini/yetkilerini günceller veya kullanıcıyı siler.
* `POST /api/admin/users/{id}/password` — Kullanıcının şifresini sıfırlar.
* `GET / DELETE /api/admin/users/{id}/history` — Kullanıcının işlem geçmişini görüntüler veya temizler.
* `GET / POST / PUT / DELETE /api/admin/shared-data` — Ortak veri arşivindeki kayıtlar üzerinde CRUD işlemleri yapar.
