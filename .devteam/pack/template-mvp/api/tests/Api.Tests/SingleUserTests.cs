using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Features.Auth;
using MyApp.Api.Features.Customers;
using MyApp.Api.Shared.Http;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests;

/// <summary>
/// By default the app is single-user: no sign-in, every request is the one local owner. The sign-in code is still there, switched off,
/// and AUTH_MODE=multi turns it on. These tests each use storage of their own, so they cannot disturb the shared one.
/// </summary>
public sealed class SingleUserTests
{
    private static void Single(Microsoft.AspNetCore.Hosting.IWebHostBuilder b) => b.UseSetting("Auth:Mode", "single");

    private static void Multi(Microsoft.AspNetCore.Hosting.IWebHostBuilder b) => b.UseSetting("Auth:Mode", "multi");

    [Fact]
    public async Task Single_Me_WorksWithoutSigningIn()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        var client = storage.Start(Single);

        var response = await client.GetAsync("/api/auth/me");
        var me = await response.Content.ReadFromJsonAsync<UserDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("local@localhost", me!.Email);
        Assert.True(me.SingleUser, "the web app is told, so it can hide sign-in and sign-out");
    }

    [Fact]
    public async Task Single_CustomersWork_WithoutSigningIn()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        var client = storage.Start(Single);

        var created = await client.PostAsync("/api/customers", new CustomerRequest("Ada", "ada@example.com"));
        var list = await client.Http.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(["Ada"], list!.Items.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Single_RegisterAndLogin_AreNotThere()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        var client = storage.Start(Single);

        Assert.Equal(HttpStatusCode.NotFound, (await client.RegisterAsync(TestApp.UniqueEmail(), TestApp.RandomPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.LoginAsync(TestApp.UniqueEmail(), TestApp.RandomPassword())).StatusCode);
    }

    [Fact]
    public async Task Mode_Endpoint_SaysWhichMode_ToAnyone()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        var single = await storage.Start(Single).Http.GetFromJsonAsync<AuthModeDto>("/api/auth/mode");
        var multi = await storage.Start(Multi).Http.GetFromJsonAsync<AuthModeDto>("/api/auth/mode");

        Assert.Equal("single", single!.Mode);
        Assert.Equal("multi", multi!.Mode);
    }

    [Fact]
    public async Task Multi_StillRequiresSignIn()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        var client = storage.Start(Multi);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Upgrade_TheFirstPersonToRegister_TakesOverTheLocalData()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        var single = storage.Start(Single);
        (await single.PostAsync("/api/customers", new CustomerRequest("Ada", "ada@example.com"))).EnsureSuccessStatusCode();

        var multi = storage.Start(Multi);
        var registered = await multi.RegisterAsync("owner@example.com", TestApp.RandomPassword());
        var me = await registered.Content.ReadFromJsonAsync<UserDto>();
        var list = await multi.Http.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers");

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(AuthMode.LocalOwnerId, me!.Id);
        Assert.Equal("owner@example.com", me.Email);
        Assert.Equal(["Ada"], list!.Items.Select(c => c.Name).ToArray());

        var second = storage.Start(Multi);
        var other = await (await second.RegisterAsync("someone@example.com", TestApp.RandomPassword())).Content.ReadFromJsonAsync<UserDto>();
        var theirs = await second.Http.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers");
        Assert.NotEqual(AuthMode.LocalOwnerId, other!.Id);
        Assert.Empty(theirs!.Items);
    }

    [Fact]
    public async Task Upgrade_TakingOverCanBeSwitchedOff()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        (await storage.Start(Single).PostAsync("/api/customers", new CustomerRequest("Ada", "ada@example.com"))).EnsureSuccessStatusCode();

        var multi = storage.Start(b => { Multi(b); b.UseSetting("Auth:ClaimLocalData", "false"); });
        var me = await (await multi.RegisterAsync("owner@example.com", TestApp.RandomPassword())).Content.ReadFromJsonAsync<UserDto>();
        var list = await multi.Http.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers");

        Assert.NotEqual(AuthMode.LocalOwnerId, me!.Id);
        Assert.Empty(list!.Items);
    }

    [Fact]
    public async Task Single_RefusesStorageThatAlreadyHasPeople()
    {
        await using var storage = await IsolatedApp.CreateAsync();
        (await storage.Start(Multi).RegisterAsync("real@example.com", TestApp.RandomPassword())).EnsureSuccessStatusCode();

        var error = Assert.ThrowsAny<Exception>(() => storage.Start(Single));

        Assert.Contains("AUTH_MODE=multi", error.ToString());
    }

    [Fact]
    public void Production_RefusesSingleUser_UnlessTold()
    {
        var error = Assert.Throws<AuthSetupException>(() => AuthMode.Validate(Config(), Env("Production")));

        Assert.Contains("AUTH_MODE=multi", error.Message);
        Assert.Contains("Auth__AllowSingleUserInProduction", error.Message);
    }

    [Fact]
    public void Production_AllowsSingleUser_WhenToldTo_AndMultiAlways()
    {
        AuthMode.Validate(Config(("Auth:AllowSingleUserInProduction", "true")), Env("Production"));
        AuthMode.Validate(Config(("Auth:Mode", "multi")), Env("Production"));
        AuthMode.Validate(Config(), Env("Development"));
    }

    [Fact]
    public void AnUnknownMode_IsExplained()
    {
        var error = Assert.Throws<AuthSetupException>(() => AuthMode.Validate(Config(("AUTH_MODE", "everyone")), Env("Development")));

        Assert.Contains("single", error.Message);
        Assert.Contains("multi", error.Message);
    }

    [Fact]
    public void TheEnvironmentSettingWinsOverTheSettingsFile()
    {
        Assert.True(AuthMode.IsSingle(Config()), "single is the default");
        Assert.False(AuthMode.IsSingle(Config(("Auth:Mode", "multi"))));
        Assert.False(AuthMode.IsSingle(Config(("AUTH_MODE", "multi"), ("Auth:Mode", "single"))));
        Assert.True(AuthMode.IsSingle(Config(("AUTH_MODE", "single"), ("Auth:Mode", "multi"))));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static IHostEnvironment Env(string name) => new FakeEnvironment(name);

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
