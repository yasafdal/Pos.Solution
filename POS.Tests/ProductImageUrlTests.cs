using POS.Shared.Helpers;

namespace POS.Tests;

public static partial class Program
{
    private static Task TestProductImageUrlResolution()
    {
        const string uat = "https://dev-uat.agressolutions.com/";
        const string expected = "https://dev-uat.agressolutions.com/uploads/products/f769354d-a2f7-4673-8fd1-af6ec7095425.jpg";
        const string file = "uploads/products/f769354d-a2f7-4673-8fd1-af6ec7095425.jpg";

        void Eq(string? image, string? baseUrl, string want, string why) =>
            Assert(ProductImageUrl.Resolve(image, baseUrl) == want, $"{why}: got '{ProductImageUrl.Resolve(image, baseUrl)}', want '{want}'");

        Eq("/" + file, uat, expected, "leading slash + trailing slash base");
        Eq(file, uat, expected, "no leading slash");
        Eq("/" + file, uat.TrimEnd('/'), expected, "base without trailing slash");
        Eq("//" + file, "https://dev-uat.agressolutions.com//", "//" + file, "protocol-relative stays untouched");
        Eq("http://127.0.0.1:5001/" + file, uat, expected, "stored loopback host rebased onto UAT");
        Eq("http://localhost:5032/" + file + "?v=2", uat, expected + "?v=2", "loopback with query rebased");
        Eq("https://cdn.example.com/a.png", uat, "https://cdn.example.com/a.png", "external absolute URL never rewritten");
        Eq(expected, uat, expected, "already-UAT URL not double-prefixed");
        Eq("   ", uat, "", "blank");
        Eq(null, uat, "", "null");
        Eq("/" + file, null, "/" + file, "missing base keeps relative path");
        Eq("/" + file, "not a url", "/" + file, "invalid base keeps relative path");
        Assert(!ProductImageUrl.Resolve("/" + file, uat).Contains("127.0.0.1"), "UAT output must not reference 127.0.0.1");
        return Task.CompletedTask;
    }
}
