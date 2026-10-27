using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Browser security headers on every response (audit G-01).
///
/// Enforced now: no framing (stops another site loading the dashboard in a hidden frame and tricking a
/// manager into clicking Resolve/Approve), no MIME sniffing, a referrer that doesn't leak page paths
/// to other sites, and no camera/microphone/location for anything but this site.
///
/// The Content-Security-Policy is sent as Report-Only first: the browser reports what it would block
/// (DevTools console) but blocks nothing. Once a week of normal use shows no reports, rename the header
/// to "Content-Security-Policy" to enforce it. Its sources are what the dashboard loads today:
/// Firebase JS (www.gstatic.com) and its Google APIs, tesseract.js / face-api.js (cdn.jsdelivr.net, plus
/// the tesseract language data), MapTiler, and Firebase Storage photos.
/// </summary>
public static class SecurityHeaders
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        // Inline scripts in App.razor (theme, Blazor hooks); OCR libraries compile WebAssembly.
        "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' https://www.gstatic.com https://cdn.jsdelivr.net; " +
        "worker-src 'self' blob: https://cdn.jsdelivr.net; " +
        // MudBlazor and the components set inline styles.
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https://firebasestorage.googleapis.com https://api.maptiler.com; " +
        "font-src 'self' data:; " +
        "connect-src 'self' wss: https://*.googleapis.com https://www.gstatic.com https://cdn.jsdelivr.net " +
            "https://tessdata.projectnaptha.com https://api.maptiler.com; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseLargaSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers["X-Frame-Options"] = "DENY";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
            headers["Content-Security-Policy-Report-Only"] = ContentSecurityPolicy;
            await next();
        });
}
