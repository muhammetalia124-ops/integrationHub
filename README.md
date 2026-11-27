# IntegrationHub

Döviz, altın, hava durumu, namaz vakitleri ve konum çözümleme verilerini tek yerden sunan bir .NET 8 web uygulaması. Dış servis yanıtları önbelleğe alınır ve ortak bir arşive yazılır, böylece aynı veri için tekrar tekrar dış API çağrısı yapılmaz.

Arayüz `wwwroot` altındaki tek sayfalık bir JavaScript uygulamasıdır.

---

## Özellikler

### Entegrasyonlar

- **Döviz:** TCMB günlük kurları. TRY dahil tüm para birimi çiftleri arasında çapraz kur ile dönüşüm yapılabilir.
- **Altın:** Tek kaynak Truncgil Finans (v4 today.json). Tüm altın türleri ve ons altın doğrudan bu kaynaktan alınır; yedek servis veya hesaplama kullanılmaz. Her altın türünde "Miktar → TL" ve "Nakit → adet/gram" modları vardır.
- **Hava durumu:** Open-Meteo geocoding ve forecast; yanıt yoksa wttr.in.
- **Namaz vakitleri:** Diyanet verileri üzerinden il/ilçe bazında günlük vakitler.
- **Konum çözümleme:** Tarayıcı koordinatları BigDataCloud ile il/ilçeye çevrilir, olmazsa OpenStreetMap Nominatim denenir. Bulut sağlayıcıları Nominatim isteklerini sık engellediği için BigDataCloud birincil yapıldı.

Yeni bir entegrasyon eklemek için `IIntegrationService` uygulayan bir sınıf yazmak yeterli; servisler reflection ile otomatik kaydedilir ve form alanları entity üzerinden üretilir.

### Önbellek ve ortak veri arşivi

Dış çağrıları azaltmak için `SemaphoreSlim` ile korunan veritabanı önbelleği kullanılır. Eşzamanlı istekler aynı anda dış servisi dövmez.

| Veri | Süre |
| :--- | :--- |
| Döviz kurları (TCMB) | 5 dakika |
| Altın fiyatları | 5 dakika |
| Hava durumu (güncel) | 3 saat |
| Namaz vakitleri (günlük) | 24 saat |
| Konum çözümleme | 30 gün |
| Hava durumu şehir koordinatları | 30 gün |

Çekilen günlük veriler `Cache.SharedApiData` tablosuna yazılır. TCMB kurları her yenilemede tüm para birimleriyle birlikte kaydedilir. Yöneticinin düzenlediği kayıtlar dış API güncellemeleriyle ezilmez ve o günkü hesaplamada öncelikli kullanılır.

### Kimlik doğrulama

- Şifreler BCrypt (`workFactor: 11`) ile saklanır, oturumlar JWT ile yürütülür.
- **E-posta doğrulama:** Kayıt sonrası kullanıcıya 6 haneli kod ve tek tıkla doğrulama bağlantısı gönderilir. Doğrulanmamış hesap giriş yapamaz (`403`, `detail: email-not-verified`).
- **Şifremi unuttum:** E-posta ile sıfırlama bağlantısı/kodu gönderilir. Şifre sıfırlanınca kullanıcının açık refresh token'ları silinir.
- **Yenilenebilir JWT:** Access token 15 dakika geçerlidir. Süre dolmadan 60 saniye önce istemci refresh token ile oturumu arka planda yeniler; herhangi bir istek `401` alırsa bir kez yenilenip tekrar denenir. Refresh token rotasyonludur: yenilemede eskisi veritabanından silinir, yerine yenisi yazılır. Geçerliliği 7 gündür.
- **Kullanıcı adı değişikliği:** Hesap Ayarları'ndan 30 günde bir kez yapılabilir. Değişiklik sonrası istemciye yeni Access + Refresh token çifti döner, çünkü eski JWT içindeki `unique_name` eski adı taşımaya devam eder.
- **Misafir girişi:** Bilgi istenmeden tek tıkla açılır. Refresh token verilmez, kimlik `UserId = 0` ile taşınır ve veritabanına hiçbir kayıt yazılmaz. Geçmiş ekranında "Lütfen kayıt olun" uyarısı görünür.
- **Giriş deneme limiti:** Aynı e-posta için 3 hatalı girişte hesap 15 dakika bloke edilir (`429`). Başarılı girişte sayaç sıfırlanır.
- Yönetici işlemleri, veritabanından anlık kontrol yapan `AdminAuthorizationFilter` ile korunur.

### İşlem geçmişi

Kullanıcı hesaplamaları kişisel geçmişe idempotent şekilde yazılır (aynı sorgu tekrar kayıt oluşturmaz). Kullanıcı kendi geçmişini temizleyebilir.

---

## Mimari

Sorumluluklar üç katmana ayrıldı:

```text
Controllers/            API uç noktaları
Middlewares/            JWT doğrulama, admin yetki filtresi, global hata yönetimi
Domain/                 Çekirdek katman (EF Core bağımlılığı yok)
├── Entities/           Veritabanı varlıkları
├── DTOs/               İstek/yanıt modelleri
├── Contracts/          Servis ve repository arayüzleri
└── Services/           İş kuralları
Infrastructure/         Dış dünya ve teknik altyapı
├── Persistence/        DbContext, configuration, migration, repository, başlatıcı
├── Integrations/       Döviz, altın, hava durumu, namaz, konum
└── Security/           BCrypt, JWT üretimi, Brevo/Resend HTTP e-posta gönderimi
wwwroot/                Tek sayfalık arayüz
```

Bağımlılık yönü tek yönlü:

```text
Web (Program.cs, Controllers, Middlewares) → Infrastructure → Domain
```

Domain içinde EF Core veya Npgsql paketi yoktur. Infrastructure, tüm EF Core işlerini ve veritabanı başlatmayı üstlenir. Web katmanı servisleri yalnızca `AddInfrastructure(builder.Configuration)` ile kaydeder; bu metot önce `AddPersistence`, sonra `AddDomain` çağırır.

`AuthService`, `AdminService` ve `UserActivityService` DbContext'e doğrudan bağlanmaz; veriye `IUserRepository` ve `IUserActivityRepository` üzerinden erişir.

### Veritabanı şeması

PostgreSQL üzerinde üç mantıksal şema:

| Şema | DbContext | Tablolar |
| :--- | :--- | :--- |
| `Auth` | `UserDbContext` | `Users`, `AuthVerificationCodes`, `RefreshTokens` |
| `Activity` | `UserActivityDbContext` | `UserActivities` |
| `Cache` | `CurrencyRateDbContext` | `CurrencyRates`, `PrayerTimeCache`, `ExternalApiCache`, `SharedApiData` |

Migration komutları Infrastructure katmanından çalıştırılır:

```bash
dotnet ef migrations add <Ad> --project Infrastructure --startup-project Infrastructure --context UserDbContext
```

Uygulama açılışında eksik auth tablo ve kolonları idempotent olarak tamamlanır (`AuthSchemaUpgrade`), mevcut tablolara dokunulmaz.

---

## Kurulum

Gerekli: .NET 8 SDK, PostgreSQL 14+ veya Docker.

### Ortam değişkenleri

Yerelde `dotnet run` kullanıcı sırlarını (user-secrets) okur. Docker Compose `.env` dosyasını okur. `.env` git'e eklenmez.

| Değişken | Zorunlu | Açıklama |
| :--- | :--- | :--- |
| `ConnectionStrings__UserDbConnection` | ✅ | Kullanıcı veritabanı (PostgreSQL) |
| `ConnectionStrings__ActivityDbConnection` | ✅ | İşlem geçmişi veritabanı |
| `ConnectionStrings__CurrencyRatesConnection` | ✅ | Cache ve ortak veri arşivi veritabanı |
| `JwtSettings__SecretKey` | ✅ | İmza anahtarı, en az 32 bayt. Boşsa uygulama başlamaz |
| `JwtSettings__Issuer` | — | Varsayılan `IntegrationHub` |
| `JwtSettings__Audience` | — | Varsayılan `IntegrationHubUsers` |
| `JwtSettings__AccessTokenMinutes` | — | Varsayılan `15` |
| `JwtSettings__RefreshTokenDays` | — | Varsayılan `7` |
| `AccountSettings__UsernameChangeIntervalDays` | — | Varsayılan `30` |
| `EmailSettings__Provider` | — | `auto`, `brevo` veya `resend`. Varsayılan `auto` (Brevo) |
| `EmailSettings__ApiKey` | ✅ | Brevo (`xkeysib-...`) veya Resend (`re_...`) API anahtarı |
| `EmailSettings__FromEmail` | ✅ | Sağlayıcıda doğrulanmış gönderen adresi |
| `EmailSettings__FromName` | — | Varsayılan `IntegrationHub` |
| `EmailSettings__PublicAppUrl` | ✅ | Doğrulama ve sıfırlama bağlantılarının üretildiği adres |
| `EmailSettings__VerificationCodeMinutes` | — | Kod geçerliliği, varsayılan `30` |
| `AdminBootstrap__Email` | ✅ | Yönetici yoksa oluşturulacak hesabın e-postası |
| `AdminBootstrap__Username` | ✅ | İlk yönetici görünen adı |
| `AdminBootstrap__Password` | ✅ | İlk yönetici şifresi |
| `AppSecret` | — | `auto-login` için `X-App-Secret` başlık değeri |
| `Cors__AllowedOrigins__0`, `__1`, … | — | İzinli origin listesi. Boşsa tüm originlere izin verilir |
| `API_PORT` | — | Docker Compose'da dışa açılan port, varsayılan `8080` |
| `ASPNETCORE_ENVIRONMENT` | — | `Development` veya `Production` |
| `ASPNETCORE_URLS` | — | Render'da `http://+:$PORT` |

```bash
dotnet user-secrets set "EmailSettings:Provider" "brevo"
dotnet user-secrets set "EmailSettings:ApiKey" "xkeysib-..."
dotnet user-secrets set "EmailSettings:FromEmail" "ornek@alanadi.com"
dotnet user-secrets set "EmailSettings:PublicAppUrl" "https://ornek.onrender.com"
dotnet user-secrets set "AppSecret" "<deger>"
dotnet user-secrets set "Cors:AllowedOrigins:0" "http://localhost:3000"
```

`ApiKey` girilmezse e-posta gönderilmez. Development ortamında doğrulama kodu kayıt yanıtında döner, Production'da dönmez.

### E-posta gönderimi

E-postalar yalnızca **HTTP API** üzerinden gönderilir; SMTP sunucusuna hiç bağlanılmaz. Böylece Render gibi 587/465 portu çıkışını engelleyen ortamlarda da çalışır. Sağlayıcı `EmailSettings__Provider` ile seçilir:

| Provider | Davranış |
| :--- | :--- |
| `auto` | `ApiKey` mevcut sağlayıcıyla Brevo varsayılarak gönderilir |
| `brevo` | Brevo HTTP API (`POST https://api.brevo.com/v3/smtp/email`) |
| `resend` | Resend HTTP API (`POST https://api.resend.com/emails`) |

`ApiKey` veya `FromEmail` boşsa e-posta gönderilmez, durum uyarı olarak loglanır ve istek düşmez. API hatası dönerse hata loglanır; başka bir sağlayıcıya otomatik geçiş yapılmaz.

```bash
# Brevo
EmailSettings__Provider=brevo
EmailSettings__ApiKey=xkeysib-xxxxxxxx
EmailSettings__FromEmail=ornek@alanadi.com
EmailSettings__PublicAppUrl=https://ornek.onrender.com

# Resend
EmailSettings__Provider=resend
EmailSettings__ApiKey=re_xxxxxxxx
EmailSettings__FromEmail=ornek@alanadi.com
```

> Seçili sağlayıcıda `FromEmail` doğrulanmış bir gönderen adresi olmalıdır. Resend'de doğrudan Gmail adresi kullanılamaz.

### Çalıştırma

```bash
dotnet restore
dotnet run
```

Açılışta `DatabaseInitializer` migration'ları uygular ve sistemde yönetici yoksa ilk yönetici hesabını oluşturur.

### Render

| Ayar | Değer |
| :--- | :--- |
| Build Command | `dotnet publish -c Release -o out` |
| Start Command | `dotnet out/IntegrationHub.dll` |
| PORT | Render tarafından atanır, uygulama `ASPNETCORE_URLS=http://+:$PORT` ile dinler |

Ortam değişkenlerini Render panelindeki **Environment** sekmesine girin. `EmailSettings__PublicAppUrl` canlı adres olmalı, aksi halde e-postadaki bağlantılar çalışmaz. Ücretsiz planda sunucu uykuya girer; ilk istek 30-50 saniye sürebilir.

### Docker

```bash
docker build -t integrationhub .
docker run -p 5080:8080 --env-file .env integrationhub
```

---

## API

### `/api/auth`

| Metot ve yol | Açıklama |
| :--- | :--- |
| `POST /register` | Kayıt oluşturur, doğrulama e-postası gönderir. JWT döndürmez |
| `POST /verify-email` | `{ email, code }` veya `{ email, token }` ile e-postayı doğrular |
| `POST /resend-verification` | Doğrulama e-postasını yeniden gönderir |
| `POST /forgot-password` | Sıfırlama bağlantısı/kodu gönderir |
| `POST /reset-password` | `{ email, code \| token, newPassword }` ile şifreyi sıfırlar |
| `POST /login` | Access + refresh token döndürür |
| `POST /refresh` | Access token'ı yeniler, refresh token'ı döndürür (rotasyonlu) |
| `POST /logout` | Refresh token'ı siler |
| `POST /guest` | Misafir oturumu açar |
| `GET /me` | Kullanıcı bilgileri (JWT gerekir) |
| `POST /change-password` | Şifreyi değiştirir, yeni token çifti döndürür (JWT gerekir) |
| `POST /change-username` | Kullanıcı adını değiştirir, yeni token çifti döndürür (JWT gerekir) |
| `POST /delete-account` | Hesabı ve geçmişi siler (JWT gerekir) |
| `POST /auto-login` | `X-App-Secret` ile harici uygulama girişi |

### `/api/search` (JWT gerekir)

| Metot ve yol | Açıklama |
| :--- | :--- |
| `GET /init` | Kategoriler ve dinamik form alanları |
| `POST /calculate` | Hesaplama yapar ve geçmişe yazar |
| `POST /location` | Koordinattan il/ilçe çözer |
| `GET /shared-data` | Ortak veri arşivi |
| `GET /history` | Kullanıcının kendi geçmişi |
| `DELETE /history` | Kendi geçmişini temizler |

### `/api/admin` (yalnızca admin)

| Metot ve yol | Açıklama |
| :--- | :--- |
| `GET`, `POST /users` | Kullanıcıları listeler, yeni kullanıcı ekler |
| `GET`, `PUT`, `DELETE /users/{id}` | Kullanıcı getirir, günceller, siler |
| `POST /users/{id}/password` | Kullanıcının şifresini sıfırlar |
| `GET`, `DELETE /users/{id}/history` | Kullanıcının geçmişini görüntüler veya temizler |
| `GET`, `POST /shared-data` | Ortak verileri listeler, kayıt ekler |
| `GET`, `PUT`, `DELETE /shared-data/{id}` | Tek kaydı getirir, günceller, siler |
| `DELETE /shared-data?type=&from=&to=` | Filtreye uyan kayıtları toplu siler |

---

## Notlar

- `.env`, user-secrets ve Render ortam değişkenlerinde gerçek şifreler bulunur; bu değerleri git'e eklemeyin.
- `JwtSettings__SecretKey` değişirse tüm oturumlar kapanır.
- Yerelde `dotnet run` `.env` dosyasını okumaz; değerleri user-secrets üzerinden tanımlayın.
