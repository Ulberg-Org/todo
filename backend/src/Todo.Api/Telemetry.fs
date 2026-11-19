/// Traces, metrics and logs over OTLP (D12): to the local otel-lgtm in tier 1, to the cluster's collector
/// later. Where to is configuration (OTEL_EXPORTER_OTLP_ENDPOINT), not code.
module Todo.Api.Telemetry

open Microsoft.Extensions.DependencyInjection
open OpenTelemetry
open OpenTelemetry.Logs
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Npgsql
open Todo.Api.BuildInfo

let serviceName = "todo-backend"

/// Registers OpenTelemetry for the app, the database client and the runtime. Every signal carries the
/// service, the commit and the instance, so two instances (D57) are always told apart.
let add (build: BuildInfo) (services: IServiceCollection) : IServiceCollection =
    services
        .AddOpenTelemetry()
        .ConfigureResource(fun resource ->
            resource.AddService(serviceName, serviceVersion = build.commit, serviceInstanceId = build.instance)
            |> ignore)
        .WithTracing(fun tracing ->
            tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql()
            |> ignore)
        .WithMetrics(fun metrics ->
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation()
            |> ignore)
        .WithLogging()
        .UseOtlpExporter()
        .Services
