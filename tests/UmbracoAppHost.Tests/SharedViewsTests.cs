using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using UmbracoAppHost.Hosting;
using Xunit;

namespace UmbracoAppHost.Tests;

public sealed class SharedViewsTests
{
    [Fact]
    public void Each_view_name_is_written_once_from_the_last_library_that_ships_it()
    {
        var root = Directory.CreateTempSubdirectory("apphost-shared-views-");
        try
        {
            static PhysicalFileProvider Library(string folder, params (string Name, string Text)[] views)
            {
                Directory.CreateDirectory(Path.Combine(folder, "Views"));
                foreach (var (name, text) in views) File.WriteAllText(Path.Combine(folder, "Views", name), text);
                return new PhysicalFileProvider(folder);
            }
            using var first = Library(Path.Combine(root.FullName, "first"), ("Shared.cshtml", "first"), ("OnlyFirst.cshtml", "only first"), ("_ViewImports.cshtml", "@using First"));
            using var second = Library(Path.Combine(root.FullName, "second"), ("Shared.cshtml", "second"), ("_ViewImports.cshtml", "@using Second"));
            var site = Path.Combine(root.FullName, "site");
            var views = Path.Combine(site, "Views");
            Directory.CreateDirectory(views);
            // A stale copy of the overridden view, the host's own imports and a view of the host's that no library ships.
            File.WriteAllText(Path.Combine(views, "Shared.cshtml"), "stale");
            File.WriteAllText(Path.Combine(views, "_ViewImports.cshtml"), "@using Host");
            File.WriteAllText(Path.Combine(views, "Own.cshtml"), "own");
            var environment = new Environment(site);
            var logger = new ListLogger();

            SharedViews.EnsureTemplates(environment, logger, [first, second]);
            Assert.Equal("second", File.ReadAllText(Path.Combine(views, "Shared.cshtml")));
            Assert.Equal("only first", File.ReadAllText(Path.Combine(views, "OnlyFirst.cshtml")));
            Assert.Equal("@using Host", File.ReadAllText(Path.Combine(views, "_ViewImports.cshtml")));
            Assert.Equal("own", File.ReadAllText(Path.Combine(views, "Own.cshtml")));
            // The overridden view was rewritten once, not once for each library that ships it.
            Assert.EndsWith(": Shared.cshtml.", Assert.Single(logger.Messages));

            var written = Directory.GetFiles(views).ToDictionary(path => path, File.GetLastWriteTimeUtc);
            foreach (var path in written.Keys) File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            SharedViews.EnsureTemplates(environment, logger, [first, second]);
            Assert.Single(logger.Messages);
            Assert.All(Directory.GetFiles(views), path => Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path)));
            Assert.Equal(written.Keys.Order(), Directory.GetFiles(views).Order());
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Shared views tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
