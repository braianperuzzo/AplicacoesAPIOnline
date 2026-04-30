using AplicacoesOnline.Models.ShortLinks;

namespace AplicacoesOnline.Services.ShortLinks;

public interface IShortLinksService
{
    (ShortLinkCreateResponse? Response, Dictionary<string, string[]>? Errors) Create(ShortLinkCreateRequest request);

    ShortLinkResolveResult Resolve(string token);
}
