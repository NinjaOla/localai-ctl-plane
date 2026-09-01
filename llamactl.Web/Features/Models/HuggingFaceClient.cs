using System.Net.Http.Json;
using System.Text.Json.Serialization;
using llamactl.Contracts;

namespace llamactl.Web.Features.Models;

internal sealed class HuggingFaceClient(HttpClient client)
{
    public async Task<IReadOnlyList<HuggingFaceRepository>> SearchAsync(string query, CancellationToken token)
    {
        var models = await client.GetFromJsonAsync<List<HfModel>>($"api/models?search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&direction=-1&limit=20", token) ?? [];
        return models.Select(model => new HuggingFaceRepository(model.Id, model.Downloads, model.Likes, model.LastModified)).ToList();
    }

    public async Task<IReadOnlyList<HuggingFaceFile>> FilesAsync(string repository, CancellationToken token)
    {
        var model = await client.GetFromJsonAsync<HfModel>($"api/models/{repository}?blobs=true", token);
        return model?.Siblings?.Where(file => file.Rfilename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(file => new HuggingFaceFile(file.Rfilename, file.Size ?? file.Lfs?.Size ?? 0)).OrderBy(file => file.Path).ToList() ?? [];
    }

    private sealed record HfModel(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("downloads")] long Downloads,
        [property: JsonPropertyName("likes")] long Likes,
        [property: JsonPropertyName("lastModified")] DateTimeOffset? LastModified,
        [property: JsonPropertyName("siblings")] IReadOnlyList<HfSibling>? Siblings);
    private sealed record HfSibling(
        [property: JsonPropertyName("rfilename")] string Rfilename,
        [property: JsonPropertyName("size")] long? Size,
        [property: JsonPropertyName("lfs")] HfLfs? Lfs);
    private sealed record HfLfs([property: JsonPropertyName("size")] long Size);
}