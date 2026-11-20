/// Traces, metrics and logs over OTLP (D12): to the local otel-lgtm in tier 1, to the cluster's collector
/// later. Where to is configuration (OTEL_EXPORTER_OTLP_ENDPOINT), not code.
module TodoBackend.Telemetry

open Microsoft.Extensions.DependencyInjection
open OpenTelemetry
open OpenTelemetry.Logs
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Npgsql
open Core.BuildInfo

/// Registers OpenTelemetry for the app, the database client and the runtime. Every signal carries the
/// service, the commit and the instance, so two instances (D57) are always told apart. The service's name and
/// its app (service.namespace) come from the platform (OTEL_SERVICE_NAME, OTEL_RESOURCE_ATTRIBUTES), the same in
/// traces, metrics and logs (D71). Metrics carry exemplars: a sample request's trace on a measurement.
let add (build: BuildInfo) (serviceName: string) (services: IServiceCollection) : IServiceCollection =
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
                .SetExemplarFilter(ExemplarFilterType.TraceBased)
            |> ignore)
        .WithLogging()
        .UseOtlpExporter()
        .Services
