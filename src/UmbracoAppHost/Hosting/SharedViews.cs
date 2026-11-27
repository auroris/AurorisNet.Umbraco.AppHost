using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;
using Microsoft.Extensions.FileProviders;
using AppBlocks.Publishing;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Hosting;

/// <summary>
/// Packaged Razor views. Umbraco renders templates from the host's Views folder, so the top-level views the library
/// ships are written there and rewritten whenever they differ from the packaged ones; the library's file list says
/// which views are its own, and the host's other views are never touched.
/// </summary>
internal static class SharedViews
{
    private const string Imports = "_ViewImports.cshtml";

    private static readonly IFileProvider Files = new ManifestEmbeddedFileProvider(typeof(SharedViews).Assembly);

    public static void Configure(IServiceCollection services)
    {
        services.Configure<MvcOptions>(options => options.Filters.Add(new PageCacheResponseFilter()));

        // Umbraco 18 uses runtime compilation for templates edited in the backoffice.
#pragma warning disable ASPDEPR003
        services.AddOptions<MvcRazorRuntimeCompilationOptions>().Configure<IWebHostEnvironment, IEnumerable<ThemeLibrary>>((options, environment, libraries) =>
        {
            options.FileProviders.Add(Files);
            foreach (var library in libraries) options.FileProviders.Add(library.Files);
            EnsureImports(environment);
        });
#pragma warning restore ASPDEPR003
    }

    private static void EnsureImports(IWebHostEnvironment environment)
    {
        // Umbraco writes registered templates to the host. Keep their imports available to
        // both runtime compilation and the host's next normal Visual Studio build.
        var imports = Path.Combine(environment.ContentRootPath, "Views", Imports);
        if (!File.Exists(imports))
            WriteAtomic(imports, Files.GetFileInfo("Views/" + Imports).CreateReadStream, overwrite: false);
    }

    public static void EnsureTemplates(IWebHostEnvironment environment, ILogger logger, IEnumerable<ThemeLibrary> libraries)
        => EnsureTemplates(environment, logger, libraries.Select(library => library.Files).Prepend(Files));

    /// <summary>
    /// Writes each view name's winning version: this library's first, then each theme library's in order, so the last that ships
    /// a name wins, as a later library overrides an earlier one. Deciding first writes an overridden view at most once, so a start
    /// neither rewrites it twice nor reports it rewritten when the folder already holds the winner.
    /// </summary>
    internal static void EnsureTemplates(IWebHostEnvironment environment, ILogger logger, IEnumerable<IFileProvider> sources)
    {
        // uSync exports template registrations, not their Razor bodies. Supply the views
        // before importing those registrations so Umbraco never writes empty template stubs.
        var winners = new Dictionary<string, IFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in sources.SelectMany(files => files.GetDirectoryContents("Views"))
            .Where(file => !file.IsDirectory && file.Name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)))
            winners[file.Name] = file;
        var rewritten = new List<string>();
        foreach (var file in winners.Values)
        {
            var path = Path.Combine(environment.ContentRootPath, "Views", file.Name);
            var exists = File.Exists(path);
            // A host may add its own usings to the imports.
            if (exists && (file.Name.Equals(Imports, StringComparison.OrdinalIgnoreCase) || Text(File.ReadAllText(path)) == Text(Read(file)))) continue;
            WriteAtomic(path, file.CreateReadStream, overwrite: exists);
            if (exists) rewritten.Add(file.Name);
        }
        if (rewritten.Count > 0)
            logger.LogInformation("Rewrote the views the library ships that differed in {Views}: {Names}.",
                Path.Combine(environment.ContentRootPath, "Views"), string.Join(", ", rewritten));
    }

    private static void WriteAtomic(string target, Func<Stream> openSource, bool overwrite)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        using var source = openSource();
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using (output) source.CopyTo(output);
            try { File.Move(temporary, target, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(target)) { }
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// A view's text as Umbraco keeps it: registering a template rewrites the byte-order mark and the line endings and
    /// drops the final line break, none of which makes the host's copy differ.
    /// </summary>
    private static string Text(string view) => view.TrimStart('﻿').Replace("\r\n", "\n").TrimEnd('\n');

    private static string Read(IFileInfo file)
    {
        using var reader = new StreamReader(file.CreateReadStream());
        return reader.ReadToEnd();
    }

    /// <summary>Whether a top-level view is in the host's Views folder or packaged here, the two places Razor finds it.</summary>
    public static bool Exists(IWebHostEnvironment environment, string name)
        => File.Exists(Path.Combine(environment.ContentRootPath, "Views", name)) || Files.GetFileInfo("Views/" + name).Exists;
}
