using System.Net;
using System.Text;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure.Security;

internal static class EmailTemplates
{
    public static string Verification(User user, string? link, string code, DateTime expiresAtUtc)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((expiresAtUtc - DateTime.UtcNow).TotalMinutes));
        return Build(
            "E-posta adresinizi doğrulayın",
            $"Merhaba {user.Username},",
            "IntegrationHub hesabınızı etkinleştirmek için e-posta adresinizi doğrulamanız gerekiyor.",
            link,
            code,
            $"Bu bağlantı {minutes} dakika geçerlidir. Bu isteği siz yapmadıysanız e-postayı yok sayabilirsiniz.");
    }

    public static string PasswordReset(User user, string? link, string code, DateTime expiresAtUtc)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((expiresAtUtc - DateTime.UtcNow).TotalMinutes));
        return Build(
            "Şifrenizi sıfırlayın",
            $"Merhaba {user.Username},",
            "Şifrenizi yenilemek için aşağıdaki bağlantıyı kullanın veya aşağıdaki kod ile devam edin.",
            link,
            code,
            $"Bu bağlantı {minutes} dakika geçerlidir. Bu isteği siz yapmadıysanız şifreniz değişmez; e-postayı yok sayabilirsiniz.");
    }

    public static string PasswordChanged(User user) =>
        Build(
            "Şifreniz değiştirildi",
            $"Merhaba {user.Username},",
            "IntegrationHub hesabınızın şifresi az önce sıfırlandı ve tüm açık oturumlar kapatıldı.",
            null,
            null,
            "Bu işlemi siz yapmadıysanız lütfen hemen yeni bir şifre sıfırlama talebi oluşturun.");

    private static string Build(
        string title,
        string greeting,
        string intro,
        string? link,
        string? code,
        string footnote)
    {
        var builder = new StringBuilder();
        builder.Append("""
            <div style="font-family:'Segoe UI',Arial,sans-serif;max-width:560px;margin:0 auto;padding:24px;color:#1e293b">
              <div style="font-size:20px;font-weight:700;color:#1d4ed8;margin-bottom:16px">IntegrationHub</div>
            """);
        builder.Append($"<h2 style=\"font-size:18px;margin:0 0 12px\">{WebUtility.HtmlEncode(title)}</h2>");
        builder.Append($"<p style=\"margin:0 0 12px;line-height:1.6\">{WebUtility.HtmlEncode(greeting)}</p>");
        builder.Append($"<p style=\"margin:0 0 16px;line-height:1.6\">{WebUtility.HtmlEncode(intro)}</p>");

        if (!string.IsNullOrWhiteSpace(link))
        {
            builder.Append(
                $"<p style=\"margin:0 0 16px\"><a href=\"{WebUtility.HtmlEncode(link)}\" " +
                "style=\"display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;" +
                "padding:12px 20px;border-radius:10px;font-weight:600\">Devam et</a></p>");
            builder.Append(
                $"<p style=\"margin:0 0 16px;font-size:12px;color:#64748b;word-break:break-all\">" +
                $"Bağlantı açılmazsa: <a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(link)}</a></p>");
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            builder.Append("<p style=\"margin:0 0 8px;font-size:13px;color:#475569\">Alternatif olarak doğrulama kodunu kullanabilirsiniz:</p>");
            builder.Append(
                $"<p style=\"margin:0 0 16px;font-size:28px;font-weight:700;letter-spacing:6px;color:#0f172a\">{WebUtility.HtmlEncode(code)}</p>");
        }

        builder.Append($"<p style=\"margin:16px 0 0;font-size:12px;color:#64748b;line-height:1.6\">{WebUtility.HtmlEncode(footnote)}</p>");
        builder.Append("</div>");
        return builder.ToString();
    }
}
