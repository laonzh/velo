using System.Text;
using Microsoft.AspNetCore.Http;

namespace Velo;

// The browser dashboard. Its files are embedded in the executable, so the AOT build serves
// them without a folder beside it.
internal static class Dashboard
{
    private const string TokenPlaceholder = "__VELO_TOKEN__";

    public static IResult Page(string token) => Results.Content(
        ReadResource("index.html").Replace(TokenPlaceholder, token),
        "text/html; charset=utf-8",
        Encoding.UTF8);

    public static IResult Asset(string name, string contentType) =>
        Results.Content(ReadResource(name), contentType, Encoding.UTF8);

    private static string ReadResource(string name)
    {
        using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Dashboard resource was not embedded.", name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
