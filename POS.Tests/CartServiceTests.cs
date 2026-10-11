using POS.Client.Services;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public sealed class CartServiceTests
{
    [Fact]
    public void Quantity_is_added_atomically_and_invalid_or_overflowing_totals_are_rejected()
    {
        var cart = new CartService(new PosSessionService());
        var product = new ProductDto { ProductId = 1, Name = "Item", Price = 2 };
        // Reflection lets the missing quantity contract fail at runtime in RED.
        var add = typeof(CartService).GetMethod("AddItem", new[] { typeof(ProductDto), typeof(int) });
        Assert.NotNull(add);
        var changes = 0;
        cart.OnChange += () => changes++;
        add.Invoke(cart, new object[] { product, 3 });
        Assert.Equal(3, cart.TotalItems);
        Assert.Equal(1, changes);
        foreach (var invalid in new[] { 0, -1, int.MaxValue })
        {
            Assert.Throws<System.Reflection.TargetInvocationException>(() => add.Invoke(cart, new object[] { product, invalid }));
            Assert.Equal(3, cart.TotalItems);
        }
    }

    [Fact]
    public void Session_changes_clear_cart_even_without_a_sale_page()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<PosSessionService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<CartService>(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(provider);
        var session = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<PosSessionService>(scope.ServiceProvider);
        var cart = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CartService>(scope.ServiceProvider);
        session.SetSession("one", "Cashier A", "Cashier", new() { BranchId = 1 });
        cart.AddItem(new() { ProductId = 1, Price = 3 });
        session.ClearSession();
        Assert.Empty(cart.Items);
        session.SetSession("two", "Cashier B", "Cashier", new() { BranchId = 2 });
        cart.AddItem(new() { ProductId = 1, Price = 3 });
        session.SetSession("three", "Cashier C", "Cashier", new() { BranchId = 3 });
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void AddItem_NewProduct_AddsToCartWithQuantityOne()
    {
        var cart = new CartService(new PosSessionService());
        var product = new ProductDto { ProductId = 1, Name = "Espresso", Price = 120m };

        cart.AddItem(product);

        Assert.Single(cart.Items);
        var item = cart.Items[0];
        Assert.Equal(1, item.ProductId);
        Assert.Equal("Espresso", item.ProductName);
        Assert.Equal(120m, item.Price);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(120m, item.LineTotal);
        Assert.Equal(120m, cart.Total);
    }

    [Fact]
    public void AddItem_ExistingProduct_IncrementsQuantity()
    {
        var cart = new CartService(new PosSessionService());
        var product = new ProductDto { ProductId = 1, Name = "Espresso", Price = 120m };

        cart.AddItem(product);
        cart.AddItem(product);

        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Items[0].Quantity);
        Assert.Equal(240m, cart.Items[0].LineTotal);
        Assert.Equal(240m, cart.Total);
    }

    [Fact]
    public void IncreaseAndDecreaseQuantity_AdjustsLineTotalAndRemovesAtZero()
    {
        var cart = new CartService(new PosSessionService());
        var p1 = new ProductDto { ProductId = 1, Name = "Espresso", Price = 120m };
        var p2 = new ProductDto { ProductId = 2, Name = "Croissant", Price = 85m };

        cart.AddItem(p1);
        cart.AddItem(p2);
        cart.IncreaseQuantity(1);

        Assert.Equal(2, cart.Items.First(i => i.ProductId == 1).Quantity);
        Assert.Equal(325m, cart.Total);

        cart.DecreaseQuantity(1);
        Assert.Equal(1, cart.Items.First(i => i.ProductId == 1).Quantity);

        cart.DecreaseQuantity(1); // Should remove p1
        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Items[0].ProductId);
        Assert.Equal(85m, cart.Total);
    }

    [Fact]
    public void RemoveItem_And_Clear_WorksProperly()
    {
        var cart = new CartService(new PosSessionService());
        cart.AddItem(new ProductDto { ProductId = 1, Name = "Item 1", Price = 50m });
        cart.AddItem(new ProductDto { ProductId = 2, Name = "Item 2", Price = 100m });

        cart.RemoveItem(1);
        Assert.Single(cart.Items);
        Assert.Equal(100m, cart.Total);

        cart.Clear();
        Assert.Empty(cart.Items);
        Assert.Equal(0m, cart.Total);
    }
}
