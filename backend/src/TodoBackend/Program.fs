/// The API as a pipeline: input (configuration, environment) → setup → run, with a report at startup.
module TodoBackend.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Oxpecker
open Core.BuildInfo
open TodoBackend.Api

/// Each API answers on its own port (D73), so the platform can route and allow each one separately: the
/// gateway only reaches the public port, and nothing but the platform reaches the management one.
type Ports = { ``public``: int; management: int }

/// Only on `port`: an endpoint group can't leak onto another API's port.
let private on (port: int) (endpoints: Endpoint list) =
    endpoints |> List.map (configureEndpoint _.RequireHost($"*:{port}"))

/// Management: what the platform asks (the kubelet's probes). Never routed by the gateway. `/version` moves to
/// the internal API once it has authentication (Phase 6).
let private management (build: BuildInfo) =
    [
        GET
            [
                route "/health/live" Health.live
                route "/health/ready" Health.ready
                route "/version" (Health.version build)
            ]
    ]

/// Public: anonymous, from the internet. Nothing yet; its OpenAPI spec comes with the first endpoint.
let private publicApi: Endpoint list = []

let endpoints (build: BuildInfo) (ports: Ports) =
    on ports.management (management build) @ on ports.``public`` publicApi

/// The configured app, so tests run exactly what production runs. Fails at startup, not on the first
/// request, if the database connection isn't configured.
let create (args: string array) : WebApplication =
    // 1. Input
    let builder = WebApplication.CreateBuilder(args)
    // Environment.GetEnvironmentVariable returns null for an unset variable, hence Option.ofObj.
    let build =
        read (Environment.GetEnvironmentVariable >> Option.ofObj) Environment.MachineName

    let port (key: string) (fallback: int) =
        builder.Configuration.GetValue<int>($"Api:{key}", fallback)

    let ports =
        {
            ``public`` = port "PublicPort" 8080
            management = port "ManagementPort" 8081
        }

    // The platform names the service (OTEL_SERVICE_NAME); outside it, it's the backend. Null when unset.
    let serviceName =
        Environment.GetEnvironmentVariable "OTEL_SERVICE_NAME"
        |> Option.ofObj
        |> Option.defaultValue "backend"

    let connectionString =
        // The configuration indexer returns null for a missing key, hence Option.ofObj.
        builder.Configuration["Database:ConnectionString"]
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> failwith "Database:ConnectionString is not configured")

    // 2. Setup
    builder.WebHost.UseUrls($"http://+:{ports.``public``}", $"http://+:{ports.management}")
    |> ignore

    let services =
        builder.Services.AddRouting().AddOxpecker().AddNpgsqlDataSource(connectionString)
        |> Telemetry.add build serviceName

    let app = builder.Build()

    app.UseRouting().Use(Problem.exceptionMiddleware).UseOxpecker(endpoints build ports).Run(Problem.notFound)

    // 3. Report
    app.Logger.LogInformation(
        "todo-backend {Commit} (built {BuiltAt}) starting as {Instance} with {Services} services; public API on {PublicPort}, management on {ManagementPort}",
        build.commit,
        build.builtAt,
        build.instance,
        services.Count,
        ports.``public``,
        ports.management
    )

    app

[<EntryPoint>]
let main args =
    (create args).Run()
    0
