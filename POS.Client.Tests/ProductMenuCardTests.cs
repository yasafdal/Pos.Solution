using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using POS.Client.Components.Shared;
using POS.Shared.Models;

namespace POS.Client.Tests;

internal static class ProductMenuCardTests
{
    public static async Task RunAsync()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(MenuDisplayMode mode, string? image = "/uploads/products/coffee.png") =>
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<ProductMenuCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ProductMenuCard.Product)] = new ProductDto { ProductId = 7, Name = "Coffee <large>", ImageUrl = image },
                    [nameof(ProductMenuCard.DisplayMode)] = mode,
                    [nameof(ProductMenuCard.CategoryColor)] = "#185132",
                    [nameof(ProductMenuCard.CartQuantity)] = 2m,
                    [nameof(ProductMenuCard.ApiBaseAddress)] = new Uri("https://pos.example/")
                }));
                return output.ToHtmlString();
            });
        var text = await Render(MenuDisplayMode.TextOnly);
        Program.Check(!text.Contains("<img") && !text.Contains("coffee.png"), "Text mode must not render/request images.");
        Program.Check(text.Contains("Coffee &lt;large&gt;") && text.Contains("#185132") && text.Contains("in-cart"), "Text card lost name, color or cart badge.");
        var photo = await Render(MenuDisplayMode.ImageAndText);
        Program.Check(photo.Contains("https://pos.example/uploads/products/coffee.png") && photo.Contains("loading=\"lazy\"") && photo.Contains("decoding=\"async\""), "Photo must resolve against API and load lazily.");
        Program.Check(photo.Contains("Coffee &lt;large&gt;") && photo.Contains("#185132"), "Image card lost name or category.");
        var missing = await Render(MenuDisplayMode.ImageAndText, null);
        Program.Check(!missing.Contains("<img") && missing.Contains("product-image-placeholder"), "Missing photo needs a local placeholder, not a request.");
        var unsafeImage = await Render(MenuDisplayMode.ImageAndText, "javascript:alert(1)");
        Program.Check(!unsafeImage.Contains("<img") && !unsafeImage.Contains("javascript:"), "Image URL must only use HTTP(S).");
        var unknown = await Render((MenuDisplayMode)99);
        Program.Check(!unknown.Contains("<img"), "Unsupported cached modes must fall back to Text Only.");
        var external = await Render(MenuDisplayMode.ImageAndText, "https://images.example/photo.jpg");
        Program.Check(external.Contains("https://images.example/photo.jpg"), "Existing absolute image URLs must work.");
        var selected = false;
        using var interactive = new CardRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await interactive.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(ProductMenuCard.Product)] = new ProductDto { ProductId = 7, Name = "Coffee", ImageUrl = "/coffee.jpg" },
                [nameof(ProductMenuCard.DisplayMode)] = MenuDisplayMode.ImageAndText,
                [nameof(ProductMenuCard.ApiBaseAddress)] = new Uri("https://pos.example/"),
                [nameof(ProductMenuCard.OnSelect)] = EventCallback.Factory.Create(new object(), () => selected = true)
            };
            var id = interactive.AddCard();
            await interactive.Render(id, parameters);
            await interactive.DispatchEventAsync(interactive.EventId(id, "onerror"), null, new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
            Program.Check(!interactive.HasElement(id, "img"), "A failed image must be removed and replaced by the placeholder.");
            await interactive.DispatchEventAsync(interactive.EventId(id, "onclick"), null, new MouseEventArgs());
            Program.Check(selected, "Card click must call the existing selection callback after photo failure.");
            parameters[nameof(ProductMenuCard.Product)] = new ProductDto { ProductId = 7, Name = "Coffee", ImageUrl = "/new-photo.jpg" };
            await interactive.Render(id, parameters);
            Program.Check(interactive.HasElement(id, "img"), "Changing the product photo must reset the failed-image state.");
            parameters[nameof(ProductMenuCard.DisplayMode)] = MenuDisplayMode.TextOnly;
            await interactive.Render(id, parameters);
            Program.Check(!interactive.HasElement(id, "img"), "Changing to Text Only must remove images.");
        });

        // Keep actual Razor output for browser layout/network checks without starting the POS application.
        var fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "menu-card-rendered.html");
        await File.WriteAllTextAsync(fixture, "<!doctype html><meta charset=\"utf-8\"><div class=\"products-grid\">" + text + photo + missing + external + "</div>");
    }

    // Exercise the real component's events without adding a UI-test package.
    private sealed class CardRenderer(IServiceProvider services, ILoggerFactory logger) : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, logger)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int AddCard() => AssignRootComponentId(new ProductMenuCard());
        public Task Render(int id, Dictionary<string, object?> parameters) => RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters));
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        public ulong EventId(int id, string name)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; i++)
                if (frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute && frames.Array[i].AttributeName == name)
                    return frames.Array[i].AttributeEventHandlerId;
            throw new InvalidOperationException("Missing event: " + name);
        }
        public bool HasElement(int id, string name)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return Enumerable.Range(0, frames.Count).Any(i => frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element && frames.Array[i].ElementName == name);
        }
    }
}
