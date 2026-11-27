using UmbracoAppHost.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace UmbracoAppHost;

/// <summary>A site requirement checked once, in a service scope, before Umbraco boots.</summary>
public interface IAppHostStartupCheck
{
    /// <summary>Report each unmet requirement. Use services that work before CMS boot.</summary>
    Task CheckAsync(AppHostStartupReport report, CancellationToken cancellationToken);
}

public static class AppHostStartupCheckExtensions
{
    /// <summary>Creates each check in its own turn so a missing dependency leaves other checks available.</summary>
    public static IServiceCollection AddAppHostStartupCheck<TCheck>(this IServiceCollection services)
        where TCheck : class, IAppHostStartupCheck
    {
        services.TryAddScoped<TCheck>();
        return services.AddSingleton(new StartupCheckRegistration(typeof(TCheck)));
    }
}

internal sealed record StartupCheckRegistration(Type CheckType);

/// <summary>Text displayed to anyone visiting the stopped site. Log private details separately.</summary>
public sealed record AppHostStartupProblem(string Description, string Remedy);

/// <summary>Only reported problems block startup. Successful checks add nothing.</summary>
public sealed class AppHostStartupReport
{
    private readonly List<AppHostStartupProblem> problems = [];
    private readonly List<AppHostStartupProblem> advisories = [];
    internal List<Exception> Details { get; } = [];
    public IReadOnlyList<AppHostStartupProblem> Problems => problems;
    public void AddProblem(string description, string remedy)
    {
        var problem = new AppHostStartupProblem(description, remedy);
        if (!problems.Contains(problem)) problems.Add(problem);
    }
    /// <summary>Show a nonblocking issue on the setup page if another requirement stops startup.</summary>
    public void AddAdvice(string description, string remedy) => advisories.Add(new(description, remedy));
    public IReadOnlyList<AppHostStartupProblem> Advisories => advisories;

    internal void Record(StartupFailure failure)
    {
        Details.Add(failure);
        foreach (var problem in failure.Problems) AddProblem(problem.Description, problem.Remedy);
    }

    internal void ThrowIfBlocked()
    {
        if (problems.Count > 0) throw new StartupFailure(this);
    }
}
