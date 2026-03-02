using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.LastFM;

public sealed class LastFMPlugin : IMetadataProvider
{
    public string PluginId => "chronicle.plugin.lastfm";
    public string Name     => "Last.fm";
    public string Version  => "1.0.0";
    public string Author   => "thegoddamnbeckster";

    public void Configure(IReadOnlyDictionary<string, string> settings)
        => throw new NotImplementedException(`TODO: implement Configure`);
    public MediaTypeSupport[] GetSupportedMediaTypes()
        => throw new NotImplementedException(`TODO: implement GetSupportedMediaTypes`);
    public PluginSettingsSchema GetSettingsSchema()
        => throw new NotImplementedException(`TODO: implement GetSettingsSchema`);
    public Task<MediaMetadata> SearchAsync(string query, CancellationToken ct = default)
        => throw new NotImplementedException(`TODO: implement SearchAsync`);
    public Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
        => throw new NotImplementedException(`TODO: implement GetByIdAsync`);
    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
        => throw new NotImplementedException(`TODO: implement GetImageAsync`);
    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => throw new NotImplementedException(`TODO: implement HealthCheckAsync`);
}