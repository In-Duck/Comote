using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Comote.Shared;
using Newtonsoft.Json.Linq;

namespace Viewer;

internal static class ClientUpdateCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static Version LatestVersion { get; private set; } =
        ProductUpdateSettings.LatestClientVersion;

    public static async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                ProductUpdateSettings.ClientManifestUrl,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var versionText = JObject.Parse(json).Value<string>("version");
            if (Version.TryParse(versionText, out var version) && version >= LatestVersion)
                LatestVersion = version;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Updater] Latest client version check failed: {ex.Message}");
        }
    }
}