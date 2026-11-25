namespace UmbracoAppHost.Authentication;

internal static class MemberSignOut
{
    public const string Cookie = "AppHost.MemberSignedOut";

    public static CookieOptions Options(HttpRequest request) => new()
    {
        HttpOnly = true, IsEssential = true, Secure = request.IsHttps, SameSite = SameSiteMode.Lax,
        Path = request.PathBase.HasValue ? request.PathBase.ToUriComponent() : "/", MaxAge = TimeSpan.FromDays(365)
    };
}
