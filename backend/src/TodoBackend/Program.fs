/// The API as a pipeline: input (configuration, environment) → setup → run, with a report at startup.
module TodoBackend.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Oxpecker
open Core.BuildInfo
open TodoBackend.Api

let endpoints (build: BuildInfo) =
    [
        GET
            [
                route "/health/live" Health.live
                route "/health/ready" Health.ready
                route "/version" (Health.version build)
            ]
    ]

/// The configured app, so tests run exactly what production runs. Fails at startup, not on the first
/// request, if the database connection isn't configured.
let create (args: string array) : WebApplication =
    // 1. Input
    let builder = WebApplication.CreateBuilder(args)
    // Environment.GetEnvironmentVariable returns null for an unset variable, hence Option.ofObj.
    let build =
        read (Environment.GetEnvironmentVariable >> Option.ofObj) Environment.MachineName

    let connectionString =
        // The configuration indexer returns null for a missing key, hence Option.ofObj.
        builder.Configuration["Database:ConnectionString"]
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> failwith "Database:ConnectionString is not configured")

    // 2. Setup
    let services =
        builder.Services.AddRouting().AddOxpecker().AddNpgsqlDataSource(connectionString)
        |> Telemetry.add build

    let app = builder.Build()

    app.UseRouting().Use(Problem.exceptionMiddleware).UseOxpecker(endpoints build).Run(Problem.notFound)

    // 3. Report
    app.Logger.LogInformation(
        "todo-backend {Commit} (built {BuiltAt}) starting as {Instance} with {Services} services",
        build.commit,
        build.builtAt,
        build.instance,
        services.Count
    )

    app

[<EntryPoint>]
let main args =
    (create args).Run()
    0
