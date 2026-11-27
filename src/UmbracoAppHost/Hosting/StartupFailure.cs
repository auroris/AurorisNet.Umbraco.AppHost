namespace UmbracoAppHost.Hosting;

internal sealed class StartupFailure : InvalidOperationException
{
    internal IReadOnlyList<AppHostStartupProblem> Problems { get; }
    internal StartupFailure(AppHostStartupProblem problem, string detail, Exception? cause = null)
        : base(detail, cause) => Problems = [problem];

    internal StartupFailure(AppHostStartupReport report)
        : base(string.Join(Environment.NewLine, report.Problems.Select(p => $"{p.Description} {p.Remedy}")),
            report.Details.Count == 0 ? null : new AggregateException(report.Details))
    {
        Problems = report.Problems.ToArray();
    }

    internal static StartupFailure Storage(string area, string path, Exception cause)
    {
        var problem = cause is UnauthorizedAccessException
            ? new AppHostStartupProblem($"The site account was denied access to {area}.",
                $"In File Explorer, open Properties → Security for {area}. Give the site account Modify access to its working folder. In IIS the default account is IIS AppPool\\ followed by the pool name.")
            : new AppHostStartupProblem($"AppHost could not use {area}.",
                "Check the path, available disk space and the site account’s file permissions, then restart the site.");
        return new(problem, $"Cannot use {area} at '{path}'.", cause);
    }
}
