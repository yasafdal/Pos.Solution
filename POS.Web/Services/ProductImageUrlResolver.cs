using POS.Shared.Helpers;

namespace POS.Web.Services;

/// <summary>Resolves product image URLs against the existing ApiBaseUrl setting (the same key the API HttpClient uses).</summary>
public class ProductImageUrlResolver(IConfiguration configuration)
{
    public string Resolve(string? imageUrl) => ProductImageUrl.Resolve(imageUrl, configuration["ApiBaseUrl"]);
    public string OnError => ProductImageUrl.OnErrorHandler;
}
