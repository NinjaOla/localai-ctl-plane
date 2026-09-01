using System.Net;
using llamactl.Web.Features.Models;

namespace llamactl.Tests.Nodes;

public sealed class HuggingFaceClientTests
{
    [Fact]
    public async Task Client_maps_repository_and_gguf_file_metadata()
    {
        var responses = new Queue<string>(
        [
            """[{"id":"org/model-GGUF","downloads":42,"likes":7,"lastModified":"2026-09-01T00:00:00Z"}]""",
            """{"id":"org/model-GGUF","downloads":42,"likes":7,"siblings":[{"rfilename":"model-Q4.gguf","size":123},{"rfilename":"README.md","size":10}]}""",
        ]);
        using var client = new HttpClient(new QueueHandler(responses)) { BaseAddress = new Uri("https://huggingface.co/") };
        var huggingFace = new HuggingFaceClient(client);

        var repository = Assert.Single(await huggingFace.SearchAsync("model", CancellationToken.None));
        var file = Assert.Single(await huggingFace.FilesAsync(repository.Id, CancellationToken.None));

        Assert.Equal("org/model-GGUF", repository.Id);
        Assert.Equal(42, repository.Downloads);
        Assert.Equal("model-Q4.gguf", file.Path);
        Assert.Equal(123, file.SizeBytes);
    }

    private sealed class QueueHandler(Queue<string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue()),
            });
    }
}