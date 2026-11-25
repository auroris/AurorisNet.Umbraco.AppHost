using AppBlocks.Publishing;
using Microsoft.Net.Http.Headers;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace UmbracoAppHost.Website;

/// <summary>A cached public page can link to a sibling whose publication or Public Access changes without touching the page itself.</summary>
public sealed class NavigationUpdates
{
    private readonly Lock refreshLock = new();
    private long validFromTicks;

    internal void Invalidate()
    {
        lock (refreshLock)
        {
            var now = DateTime.UtcNow.Ticks;
            Volatile.Write(ref validFromTicks, now - now % TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond);
        }
    }

    public void Register(HttpContext context)
    {
        var validFrom = new DateTime(Volatile.Read(ref validFromTicks), DateTimeKind.Utc);
        if (validFrom == DateTime.MinValue) return;
        // HTTP dates have whole-second precision. A conditional request in the second of the change
        // may hold an earlier menu with the same date. Send the body until the new date can be used.
        if (DateTime.UtcNow < validFrom)
        {
            if (context.Request.Headers.ContainsKey(HeaderNames.IfNoneMatch) || context.Request.Headers.ContainsKey(HeaderNames.IfModifiedSince))
                context.KeepOutOfCaches();
            validFrom = validFrom.AddSeconds(-1);
        }
        context.RegisterDependencyUpdateDate(validFrom);
    }
}

internal sealed class NavigationRefresh(NavigationUpdates updates) :
    INotificationHandler<ContentCacheRefresherNotification>, INotificationHandler<PublicAccessCacheRefresherNotification>
{
    public void Handle(ContentCacheRefresherNotification notification) => updates.Invalidate();
    public void Handle(PublicAccessCacheRefresherNotification notification) => updates.Invalidate();
}
