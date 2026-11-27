using System.Security.Cryptography;
using AurorisNet.Umbraco.ApplicationVirtualPathFix;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Configuration.Models;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

public sealed class SiteHostingTests
{
    [Theory]
    [InlineData("https://example.org/portal/sub/", null, "/portal/sub")]
    [InlineData("https://example.org/", null, null)]
    [InlineData("https://example.org/portal", "/", "/")]
    [InlineData("https://example.org/portal", "/other", "/other")]
    [InlineData("https://example.org/portal?ticket=private", null, null)]
    public void The_public_address_supplies_a_default_mount(string address, string? configured, string? expected)
    {
        using var configuration = new ConfigurationManager();
        configuration[SiteHosting.ApplicationUrl] = address;
        configuration[VirtualPathHosting.SettingKey] = configured;
        SiteHosting.ApplyVirtualPath(configuration);
        Assert.Equal(expected, configuration[VirtualPathHosting.SettingKey]);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [VirtualPathHosting.SettingKey] = "/override" });
        SiteHosting.ApplyVirtualPath(configuration);
        Assert.Equal("/override", configuration[VirtualPathHosting.SettingKey]);
    }

    [Fact]
    public void Startup_explains_the_IIS_mount_and_listener_conflicts()
    {
        var log = new StartupLog();
        var routing = new WebRoutingSettings { UmbracoApplicationUrl = "https://example.org/portal/" };
        SiteHosting.CheckAddresses(routing, "/old", "http://*:8080;http://localhost:9000/private", "/portal", log);
        Assert.Contains(log.Messages, m => m.Contains("overrides the path"));
        Assert.Contains(log.Messages, m => m.Contains("IIS mounts this application at /portal"));
        Assert.Contains(log.Messages, m => m.Contains("Kestrel listener"));
        log.Messages.Clear();
        SiteHosting.CheckAddresses(routing, "/portal", "http://*:8080/", "/portal/", log);
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void URL_warnings_name_the_setting_without_logging_private_input()
    {
        var log = new StartupLog();
        SiteHosting.CheckAddresses(new WebRoutingSettings(), null, null, "/", log);
        Assert.Contains(SiteHosting.ApplicationUrl, Assert.Single(log.Messages));
        log.Messages.Clear();
        SiteHosting.CheckAddresses(new WebRoutingSettings { UmbracoApplicationUrl = "https://example.org/?ticket=private" },
            null, "private", "/", log);
        Assert.Equal(2, log.Messages.Count);
        Assert.All(log.Messages, m => Assert.DoesNotContain("private", m));
    }

    [Fact]
    public void An_unusable_key_ring_stops_boot_with_the_original_error_and_the_pool_to_check()
    {
        var log = new StartupLog();
        SiteHosting.CheckKeyRing(new EphemeralDataProtectionProvider(), "Portal", "site/keys", log);
        Assert.Empty(log.Messages);
        var error = Assert.Throws<InvalidOperationException>(() => SiteHosting.CheckKeyRing(new BrokenProtector(), "Portal", "site/keys", log));
        Assert.IsType<CryptographicException>(error.InnerException);
        Assert.Equal(error.Message, Assert.Single(log.Messages));
        Assert.Contains("IIS pool 'Portal'", error.Message);
        Assert.Contains("site/keys", error.Message);
    }

    private sealed class BrokenProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => throw new CryptographicException();
        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException();
    }

    private sealed class StartupLog : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
