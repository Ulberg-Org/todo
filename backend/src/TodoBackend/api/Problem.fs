/// Every error a client sees is a ProblemDetails body carrying the request's trace ID, so it can be found
/// in Grafana (traces and logs) from the response alone.
module TodoBackend.Api.Problem

open System
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker

/// The W3C trace ID of the current request. Activity.Current is null outside a trace, hence Option.ofObj.
let traceId () : string option =
    Activity.Current |> Option.ofObj |> Option.map _.TraceId.ToString()

/// A ProblemDetails response with `status`, `title` and the trace ID.
let respond (status: int) (title: string) : EndpointHandler =
    fun ctx ->
        let extensions =
            traceId ()
            |> Option.map (fun id -> [ "traceId", box id ])
            |> Option.defaultValue []
            |> dict

        ctx.Write(TypedResults.Problem(title = title, statusCode = status, extensions = extensions))

/// Unmatched routes: 404 as ProblemDetails.
let notFound: RequestDelegate =
    RequestDelegate(fun ctx -> respond StatusCodes.Status404NotFound "Not found" ctx)

/// Unexpected exceptions: logged with the trace, answered with a 500 that reveals nothing but the trace ID. A
/// response that has already started can't be replaced, so that connection is aborted instead.
let exceptionMiddleware: Func<HttpContext, RequestDelegate, Task> =
    Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
        task {
            try
                return! next.Invoke ctx
            with ex ->
                ctx.GetLogger("TodoBackend").LogError(ex, "Unhandled exception")

                if ctx.Response.HasStarted then
                    ctx.Abort()
                else
                    return! respond StatusCodes.Status500InternalServerError "Internal error" ctx
        })
