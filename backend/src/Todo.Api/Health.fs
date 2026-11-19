/// Health and version endpoints: what the platform's probes and you (or an agent) ask first.
module Todo.Api.Health

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open Todo.Api.BuildInfo

/// The process is up. Never touches dependencies, so a database outage doesn't get pods restarted.
let live: EndpointHandler = %TypedResults.Ok {| status = "live" |}

/// Ready to serve: the database answers as the app's role. Otherwise 503, so the load balancer sends
/// traffic to another instance.
let ready: EndpointHandler =
    fun ctx ->
        task {
            match! Database.ping (ctx.GetService<NpgsqlDataSource>()) ctx.RequestAborted with
            | Ok() -> return! ctx.Write(TypedResults.Ok {| status = "ready" |})
            | Error reason ->
                ctx.GetLogger("Todo.Api.Health").LogWarning("Not ready: database unreachable: {Reason}", reason)
                return! Problem.respond StatusCodes.Status503ServiceUnavailable "Database unreachable" ctx
        }

/// What is running and which instance answered.
let version (build: BuildInfo) : EndpointHandler = %TypedResults.Ok build
