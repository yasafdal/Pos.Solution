using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace POS.Tests;

public class BusinessTypeTests
{
    [Fact]
    public async Task Branch_relationship_roundtrips_and_prevents_referenced_deletion()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        var created = await http.PostAsJsonAsync("api/BusinessTypes", new { type = "FNB", description = "Food" });
        int typeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var saved = await http.PostAsJsonAsync("api/Branches", new { name = "Synthetic branch", businessTypeId = typeId, trackInventory = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        int branchId = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("branchId").GetInt32();
        var branches = await http.GetFromJsonAsync<JsonElement[]>("api/Branches");
        var branch = Assert.Single(branches!);
        Assert.Equal(typeId, branch.GetProperty("businessTypeId").GetInt32());
        Assert.Equal("FNB", branch.GetProperty("businessTypeName").GetString());
        Assert.True(branch.GetProperty("trackInventory").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"api/BusinessTypes/{typeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync($"api/Branches/{branchId}", new { name = "Synthetic branch", businessTypeId = 9999 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/Branches", new { name = "Invalid branch", businessTypeId = 9999 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync($"api/Branches/{branchId}", new { name = "Synthetic branch", businessTypeId = (int?)null })).StatusCode);
        branches = await http.GetFromJsonAsync<JsonElement[]>("api/Branches");
        Assert.Equal(JsonValueKind.Null, Assert.Single(branches!).GetProperty("businessTypeId").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"api/BusinessTypes/{typeId}")).StatusCode);
    }

    [Fact]
    public async Task Crud_rejects_duplicates_updates_lists_and_deletes()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        var response = await http.PostAsJsonAsync("api/BusinessTypes", new { type = "retail", description = "Sales" });
        var item = await response.Content.ReadFromJsonAsync<JsonElement>();
        int id = item.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("api/BusinessTypes", new { type = " RETAIL ", description = "Duplicate" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync($"api/BusinessTypes/{id}", new { type = " service ", description = " Services " })).StatusCode);
        var list = await http.GetFromJsonAsync<JsonElement[]>("api/BusinessTypes");
        Assert.Equal("SERVICE", Assert.Single(list!).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"api/BusinessTypes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/BusinessTypes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"api/BusinessTypes/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("", "Valid")]
    [InlineData("   ", "Valid")]
    [InlineData("VALID", "")]
    [InlineData("VALID", "   ")]
    [InlineData(null, "Valid")]
    public async Task Required_fields_rejected(string? type, string description)
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/BusinessTypes", new { type, description })).StatusCode);
    }

    [Theory]
    [InlineData(51, 10)]
    [InlineData(10, 251)]
    public async Task Maximum_lengths_rejected(int typeLength, int descriptionLength)
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/BusinessTypes", new { type = new string('A', typeLength), description = new string('B', descriptionLength) })).StatusCode);
    }

    [Fact]
    public async Task Create_normalizes_and_get_returns_business_type()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var http = api.CreateClient();
        var response = await http.PostAsJsonAsync("api/BusinessTypes", new { type = " retail ", description = " Product sales " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RETAIL", item.GetProperty("type").GetString());
        Assert.Equal("Product sales", item.GetProperty("description").GetString());
        var get = await http.GetFromJsonAsync<JsonElement>($"api/BusinessTypes/{item.GetProperty("id").GetInt32()}");
        Assert.Equal("RETAIL", get.GetProperty("type").GetString());
    }
}
