using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Api.Models;
using Xunit;

namespace POS.Tests;

public sealed class CategoryColorTests
{
    [Fact]
    public async Task Color_roundtrips_through_create_list_get_update_and_soft_delete()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        using var created = await http.PostAsJsonAsync("api/categories/create-category",
            new { name = "Drinks", isActive = true, colorHex = "#a1b2c3" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        AssertColor(item, "#A1B2C3");
        int id = item.GetProperty("categoryId").GetInt32();
        AssertColor(await http.GetFromJsonAsync<JsonElement>($"api/categories/get-category/{id}"), "#A1B2C3");
        AssertColor(Assert.Single((await http.GetFromJsonAsync<JsonElement[]>("api/categories"))!), "#A1B2C3");

        using var updated = await http.PutAsJsonAsync($"api/categories/update-category/{id}",
            new { categoryId = id, name = "Drinks", isActive = true, colorHex = "#d4e5f6" });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        AssertColor(await http.GetFromJsonAsync<JsonElement>($"api/categories/get-category/{id}"), "#D4E5F6");
        AssertColor(Assert.Single((await http.GetFromJsonAsync<JsonElement[]>("api/categories"))!), "#D4E5F6");

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"api/categories/remove-category/{id}")).StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement[]>("api/categories"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/categories/get-category/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"api/categories/remove-category/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsJsonAsync($"api/categories/update-category/{id}",
            new { categoryId = id, name = "Deleted", isActive = true, colorHex = "#FFFFFF" })).StatusCode);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var persisted = await db.Categories.SingleAsync(c => c.CategoryId == id);
        Assert.True(persisted.IsDeleted);
        Assert.Equal("#D4E5F6", db.Entry(persisted).Property<string>("ColorHex").CurrentValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]
    [InlineData("#ABC")]
    [InlineData("123456")]
    [InlineData("#12345678")]
    [InlineData("#GG0000")]
    [InlineData(" #123456")]
    [InlineData("#123456 ")]
    [InlineData("#123456\n")]
    [InlineData("#123456;background:red")]
    public async Task Invalid_color_is_rejected_on_create_and_update_without_mutating_data(string? colorHex)
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        using var rejected = await http.PostAsJsonAsync("api/categories/create-category",
            new { name = "Invalid", isActive = true, colorHex });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("ColorHex", out _));
        Assert.Empty((await http.GetFromJsonAsync<JsonElement[]>("api/categories"))!);

        using var created = await http.PostAsJsonAsync("api/categories/create-category",
            new { name = "Keep", isActive = true, colorHex = "#123456" });
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        int id = item.GetProperty("categoryId").GetInt32();
        using var updated = await http.PutAsJsonAsync($"api/categories/update-category/{id}",
            new { categoryId = id, name = "Changed", isActive = true, colorHex });
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        var unchanged = await http.GetFromJsonAsync<JsonElement>($"api/categories/get-category/{id}");
        AssertColor(unchanged, "#123456");
        Assert.Equal("Keep", unchanged.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(null, "#2563EB")]
    [InlineData("", "#2563EB")]
    [InlineData("red", "#2563EB")]
    [InlineData("#ABC", "#2563EB")]
    [InlineData("#12345678", "#2563EB")]
    [InlineData("#GG0000", "#2563EB")]
    [InlineData(" #123456", "#2563EB")]
    [InlineData("#123456\n", "#2563EB")]
    [InlineData("#123456;background:red", "#2563EB")]
    [InlineData("#a1b2c3", "#A1B2C3")]
    [InlineData("#AbCdEf", "#ABCDEF")]
    [InlineData("#000000", "#000000")]
    [InlineData("#FFFFFF", "#FFFFFF")]
    public void Normalize_returns_only_canonical_six_digit_hex(string? input, string expected)
    {
        Assert.Equal("#2563EB", POS.Shared.Helpers.CategoryColor.Default);
        Assert.Equal(expected, POS.Shared.Helpers.CategoryColor.Normalize(input));
    }

    [Fact]
    public async Task Omitted_color_defaults_for_older_clients()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        using var response = await http.PostAsJsonAsync("api/categories/create-category", new { name = "Legacy", isActive = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertColor(item, "#2563EB");
        AssertColor(await http.GetFromJsonAsync<JsonElement>($"api/categories/get-category/{item.GetProperty("categoryId").GetInt32()}"), "#2563EB");
        Assert.Equal("#2563EB", new POS.Shared.Models.CategoryDto().ColorHex);
        Assert.Equal("#2563EB", new Category().ColorHex);
    }

    [Fact]
    public void Mapping_uses_unquoted_column_name_required_length_and_database_default()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var entity = db.Model.FindEntityType(typeof(Category))!;
        var property = entity.FindProperty("ColorHex")!;
        Assert.Equal("ColorHex", property.GetColumnName());
        Assert.False(property.IsNullable);
        Assert.Equal(7, property.GetMaxLength());
        Assert.Equal("#2563EB", property.GetDefaultValue());
    }

    [Fact]
    public async Task Reads_normalize_existing_valid_lowercase_colors_without_overwriting_them()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var category = new Category { Name = "Existing", IsActive = true, ColorHex = "#aabbcc" };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        AssertColor(await http.GetFromJsonAsync<JsonElement>($"api/categories/get-category/{category.CategoryId}"), "#AABBCC");
        AssertColor(Assert.Single((await http.GetFromJsonAsync<JsonElement[]>("api/categories"))!), "#AABBCC");
        await db.Entry(category).ReloadAsync();
        Assert.Equal("#aabbcc", category.ColorHex);
    }

    private static void AssertColor(JsonElement category, string expected)
    {
        Assert.True(category.TryGetProperty("colorHex", out var color), "Category response must include colorHex.");
        Assert.Equal(expected, color.GetString());
    }
}
