using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MyApp.Api.Features.Customers;
using MyApp.Api.Shared.Http;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests;

[Collection(TestCollection.Name)]
public sealed class HealthTests(TestApp app)
{
    [Fact]
    public async Task Health_AnswersWithoutSigningIn()
    {
        var response = await app.NewClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>The fast-MVP version keeps its data in files. They must survive a restart and hold up under simultaneous requests.</summary>
public sealed class StorageTests
{
    private static AppClient ClientOf(WebApplicationFactory<Program> factory) =>
        new(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));

    [Fact]
    public async Task Data_SurvivesARestart()
    {
        var folder = Directory.CreateTempSubdirectory("api-restart-").FullName;
        try
        {
            var email = TestApp.UniqueEmail();
            var password = TestApp.RandomPassword();
            await using (var first = TestApp.StartApp(folder))
            {
                var client = ClientOf(first);
                (await client.RegisterAsync(email, password)).EnsureSuccessStatusCode();
                (await client.PostAsync("/api/customers", new CustomerRequest("Ada", "ada@example.com"))).EnsureSuccessStatusCode();
            }

            await using var second = TestApp.StartApp(folder);
            var again = ClientOf(second);
            (await again.LoginAsync(email, password)).EnsureSuccessStatusCode();
            var list = await again.Http.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers");

            Assert.Equal(["Ada"], list!.Items.Select(c => c.Name).ToArray());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task SimultaneousCreates_AllSucceed_WithDistinctIds()
    {
        var folder = Directory.CreateTempSubdirectory("api-parallel-").FullName;
        try
        {
            await using var factory = TestApp.StartApp(folder);
            var client = ClientOf(factory);
            (await client.RegisterAsync(TestApp.UniqueEmail(), TestApp.RandomPassword())).EnsureSuccessStatusCode();

            var responses = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(i => client.PostAsync("/api/customers", new CustomerRequest($"Customer {i}", $"c{i}@example.com"))));
            var created = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<CustomerDto>()));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
            Assert.Equal(20, created.Select(c => c!.Id).Distinct().Count());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task SameEmailTwice_AtTheSameTime_OnlyOneWins()
    {
        var folder = Directory.CreateTempSubdirectory("api-duplicate-").FullName;
        try
        {
            await using var factory = TestApp.StartApp(folder);
            var client = ClientOf(factory);
            (await client.RegisterAsync(TestApp.UniqueEmail(), TestApp.RandomPassword())).EnsureSuccessStatusCode();

            var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsync("/api/customers", new CustomerRequest("Same", "same@example.com"))));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task TheDataFolder_IsCreatedOnFirstWrite_AndHoldsReadableFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "api-fresh-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using var factory = TestApp.StartApp(folder);
            (await ClientOf(factory).RegisterAsync(TestApp.UniqueEmail(), TestApp.RandomPassword())).EnsureSuccessStatusCode();

            Assert.True(File.Exists(Path.Combine(folder, "users.json")));
            Assert.DoesNotContain(".tmp", string.Concat(Directory.GetFiles(folder)));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
