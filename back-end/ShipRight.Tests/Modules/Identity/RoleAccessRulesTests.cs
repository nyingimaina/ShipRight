using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.ComposeRepo;
using ShipRight.Modules.Database;
using ShipRight.Modules.Filesystem;
using ShipRight.Modules.Identity;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Modules.RepoMaintenance;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Scheduler;
using ShipRight.Modules.Servers;
using ShipRight.Modules.Services;
using ShipRight.Modules.Ssh;
using ShipRight.Modules.System;
using ShipRight.Modules.System.WslDisk;
using ShipRight.Modules.WatchBranch;

namespace ShipRight.Tests.Modules.Identity;

// R5: the server decides, from one table, what each role may do.
[TestClass]
public class RoleAccessRulesTests
{
    [DataTestMethod]
    // Viewer: read-only
    [DataRow("GET", "/api/projects", AppRoles.Viewer)]
    [DataRow("GET", "/api/builds", AppRoles.Viewer)]
    [DataRow("GET", "/api/builds/abc/log", AppRoles.Viewer)]
    [DataRow("GET", "/api/servers", AppRoles.Viewer)]
    [DataRow("GET", "/api/resources/scripts", AppRoles.Viewer)]
    [DataRow("GET", "/api/auth/me", AppRoles.Viewer)]
    // Operator: build and deploy
    [DataRow("POST", "/api/builds/start", AppRoles.Operator)]
    [DataRow("POST", "/api/builds/abc/deploy", AppRoles.Operator)]
    [DataRow("POST", "/api/builds/abc/cancel", AppRoles.Operator)]
    [DataRow("POST", "/api/builds/abc/rollback", AppRoles.Operator)]
    [DataRow("POST", "/api/projects/abc/ssh/exec", AppRoles.Operator)]
    [DataRow("POST", "/api/projects/abc/db/backup", AppRoles.Operator)]
    [DataRow("GET", "/api/projects/abc/ssh/ops/o1/stream", AppRoles.Operator)]
    [DataRow("POST", "/api/servers/abc/ssh/exec", AppRoles.Operator)]
    // Admin: settings and servers
    [DataRow("POST", "/api/projects", AppRoles.Admin)]
    [DataRow("PUT", "/api/projects/abc", AppRoles.Admin)]
    [DataRow("DELETE", "/api/projects/abc", AppRoles.Admin)]
    [DataRow("POST", "/api/servers", AppRoles.Admin)]
    [DataRow("PUT", "/api/servers/abc", AppRoles.Admin)]
    [DataRow("DELETE", "/api/servers/abc", AppRoles.Admin)]
    [DataRow("POST", "/api/servers/abc/bootstrap", AppRoles.Admin)]
    [DataRow("POST", "/api/servers/abc/ssh-key/generate", AppRoles.Admin)]
    [DataRow("POST", "/api/resources/scripts", AppRoles.Admin)]
    [DataRow("DELETE", "/api/resources/credentials/abc", AppRoles.Admin)]
    [DataRow("GET", "/api/resources/credentials", AppRoles.Admin)]
    [DataRow("POST", "/api/scheduler/schedules", AppRoles.Admin)]
    [DataRow("GET", "/api/team", AppRoles.Admin)]
    [DataRow("POST", "/api/team", AppRoles.Admin)]
    [DataRow("PUT", "/api/team/abc/role", AppRoles.Admin)]
    [DataRow("POST", "/api/team/abc/deactivate", AppRoles.Admin)]
    [DataRow("GET", "/api/fs/list", AppRoles.Admin)]
    [DataRow("POST", "/api/system/shutdown", AppRoles.Admin)]
    public void RequiredRole_MatchesTheDecidedPermissions(string method, string path, string expected)
    {
        Assert.AreEqual(expected, RoleAccessRules.RequiredRole(method, path));
    }

    [DataTestMethod]
    // API-004, R5: registry passwords and AWS secret keys must never reach a Viewer or Operator.
    [DataRow("GET", "/api/resources/registries", AppRoles.Admin)]
    [DataRow("GET", "/api/resources/registries/abc", AppRoles.Admin)]
    [DataRow("GET", "/api/resources/aws-profiles", AppRoles.Admin)]
    [DataRow("GET", "/api/resources/aws-profiles/abc", AppRoles.Admin)]
    [DataRow("GET", "/api/resources/scripts", AppRoles.Viewer)]
    public void SecretBearingResourceReads_NeedAdmin(string method, string path, string expected)
    {
        Assert.AreEqual(expected, RoleAccessRules.RequiredRole(method, path));
    }

    [TestMethod]
    public void HealthCheckAndWebAppFiles_ArePublic()
    {
        // R5: only these need no sign-in, apart from the endpoints marked AllowAnonymous.
        Assert.IsNull(RoleAccessRules.RequiredRole("GET", "/api/health"));
        Assert.IsNull(RoleAccessRules.RequiredRole("GET", "/login"));
        Assert.IsNull(RoleAccessRules.RequiredRole("GET", "/_next/static/chunks/main.js"));
    }

    [TestMethod]
    public void HealthLookalikes_AreNotPublic()
    {
        // R5
        Assert.IsNotNull(RoleAccessRules.RequiredRole("POST", "/api/health"));
        Assert.IsNotNull(RoleAccessRules.RequiredRole("GET", "/api/health/../projects"));
        Assert.IsNotNull(RoleAccessRules.RequiredRole("GET", "/api/healthz"));
    }

    [TestMethod]
    public void UnknownApiRoute_NeedsAtLeastViewerToRead_AndOperatorToWrite()
    {
        // R5: a route nobody wrote a rule for is never open by accident.
        Assert.AreEqual(AppRoles.Viewer, RoleAccessRules.RequiredRole("GET", "/api/brand-new-thing"));
        Assert.AreEqual(AppRoles.Operator, RoleAccessRules.RequiredRole("POST", "/api/brand-new-thing"));
        Assert.AreEqual(AppRoles.Operator, RoleAccessRules.RequiredRole("DELETE", "/api/brand-new-thing/1"));
    }

    [TestMethod]
    public void RankOrder_IsAdminOverOperatorOverViewer()
    {
        // R5
        Assert.IsTrue(AppRoles.Rank(AppRoles.Admin) > AppRoles.Rank(AppRoles.Operator));
        Assert.IsTrue(AppRoles.Rank(AppRoles.Operator) > AppRoles.Rank(AppRoles.Viewer));
        Assert.IsTrue(AppRoles.Rank(AppRoles.Viewer) > AppRoles.Rank("Owner"));
        Assert.IsTrue(AppRoles.Rank(null) == 0);
    }

    [TestMethod]
    public void TemporaryPassword_AllowsOnlyChangePasswordMeAndLogout()
    {
        // R10
        Assert.IsTrue(RoleAccessRules.AllowedWhilePasswordChangeIsPending("/api/auth/change-password"));
        Assert.IsTrue(RoleAccessRules.AllowedWhilePasswordChangeIsPending("/api/auth/me"));
        Assert.IsTrue(RoleAccessRules.AllowedWhilePasswordChangeIsPending("/api/auth/logout"));
        Assert.IsFalse(RoleAccessRules.AllowedWhilePasswordChangeIsPending("/api/projects"));
        Assert.IsFalse(RoleAccessRules.AllowedWhilePasswordChangeIsPending("/api/builds/start"));
    }

    private static WebApplication MapEveryCloudRoute()
    {
        // Built the way the cloud server builds itself, so every route's parameters resolve as they do in production.
        var builder = WebApplication.CreateBuilder();
        ShipRight.Server.CloudDiRegistrar.Register(builder.Services, builder.Configuration);
        var app = builder.Build();
        app.MapIdentityAuthRoutes();
        app.MapTeamRoutes();
        app.MapSystemRoutes();
        app.MapWslDiskRoutes();
        app.MapFsRoutes();
        app.MapProjectRoutes();
        app.MapProjectSummaryRoutes();
        app.MapBuildRoutes();
        app.MapDatabaseRoutes();
        app.MapSshTerminalRoutes();
        app.MapContainerLogRoutes();
        app.MapRepoMaintenanceRoutes();
        app.MapComposeRepoRoutes();
        app.MapServerRoutes();
        app.MapServerBootstrapRoutes();
        app.MapSchedulerCoreRoutes();
        app.MapCloudSchedulerHistoryRoutes();
        app.MapSshKeyRoutes();
        app.MapServerSshKeyRoutes();
        app.MapWatchBranchCoreRoutes();
        app.MapCloudWatchBranchHistoryRoutes();
        app.MapResourceRoutes();
        app.MapPipelineRoutes();
        return app;
    }

    private static IEnumerable<RouteEndpoint> Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>();

    [TestMethod]
    public void EveryApiRoute_HasARequiredRole_ExceptTheHealthCheck()
    {
        // R5, API-001: no mapped route falls outside the rules.
        var app = MapEveryCloudRoute();
        var routes = Endpoints(app).Where(e => e.RoutePattern.RawText!.StartsWith("/api/")).ToList();
        Assert.IsTrue(routes.Count > 80, $"expected the full route table, found {routes.Count}");

        foreach (var endpoint in routes)
        {
            var path = endpoint.RoutePattern.RawText!;
            var concrete = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", "x");
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                Assert.IsNotNull(RoleAccessRules.RequiredRole(method, concrete), $"{method} {path}");
        }
    }

    [TestMethod]
    public void OnlyTheListedEndpoints_AreMarkedPublic()
    {
        // R5, API-001, security NFR: sign-in, registration, Google sign-in and password reset only.
        var app = MapEveryCloudRoute();

        var anonymous = Endpoints(app)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .OrderBy(p => p)
            .ToArray();

        CollectionAssert.AreEqual(new[]
        {
            "/api/auth/forgot-password",
            "/api/auth/google/complete",
            "/api/auth/google/start",
            "/api/auth/login",
            "/api/auth/providers",
            "/api/auth/register",
            "/api/auth/reset-password",
        }, anonymous);
    }

    [TestMethod]
    public void TeamRoutes_AreAllAdminOnly()
    {
        // R6, R7, R8
        var app = MapEveryCloudRoute();

        foreach (var endpoint in Endpoints(app).Where(e => e.RoutePattern.RawText!.StartsWith("/api/team")))
        {
            var concrete = System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText!, @"\{[^}]+\}", "x");
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
                Assert.AreEqual(AppRoles.Admin, RoleAccessRules.RequiredRole(method, concrete), $"{method} {concrete}");
        }
    }

    [TestMethod]
    public void AuthEndpoints_RequireTheirReasonedPublicMarker_AndRateLimits()
    {
        // R10: reset requests are limited so nobody can flood an inbox.
        var app = MapEveryCloudRoute();
        var forgot = Endpoints(app).Single(e => e.RoutePattern.RawText == "/api/auth/forgot-password");

        var limiter = forgot.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();
        Assert.AreEqual(AuthEndpoints.PasswordResetLimiter, limiter?.PolicyName);
    }
}
