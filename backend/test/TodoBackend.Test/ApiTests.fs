module TodoBackend.Test.ApiTests

open System.Net
open System.Net.Http
open System.Text.Json
open Expecto
open TodoBackend.Test.Server

let private json (response: HttpResponseMessage) =
    task {
        let! body = response.Content.ReadAsStringAsync()
        return JsonDocument.Parse(body).RootElement
    }

// GetString returns null for a JSON null, hence Option.ofObj.
let private text (name: string) (e: JsonElement) =
    e.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""

/// A ProblemDetails body that carries the request's 32-hex-digit W3C trace ID.
let private expectProblem (response: HttpResponseMessage) (status: HttpStatusCode) =
    task {
        Expect.equal response.StatusCode status "status"

        Expect.equal
            (response.Content.Headers.ContentType |> Option.ofObj |> Option.map _.MediaType)
            (Some "application/problem+json")
            "ProblemDetails"

        let! body = json response
        Expect.equal (body.GetProperty("status").GetInt32()) (int status) "status in the body"
        Expect.isMatch (text "traceId" body) "^[0-9a-f]{32}$" "trace ID"
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        "API"
        [
            testTask "/health/live answers without touching the database" {
                do!
                    withApi unreachable (fun client ->
                        task {
                            let! response = client.GetAsync "/health/live"
                            Expect.equal response.StatusCode HttpStatusCode.OK "200"
                            let! body = json response
                            Expect.equal (text "status" body) "live" "live"
                        })
            }

            testTask "/version says what is running and which instance answered" {
                do!
                    withApi unreachable (fun client ->
                        task {
                            let! body = client.GetAsync "/version" |> Task.bind json
                            Expect.equal (text "commit" body) "local" "no build locally"
                            Expect.isNotEmpty (text "instance" body) "instance"
                        })
            }

            testTask "/health/ready is 200 when the database answers as the app role" {
                let! container = database.Force()

                do!
                    withApi (appConnection container) (fun client ->
                        task {
                            let! response = client.GetAsync "/health/ready"
                            Expect.equal response.StatusCode HttpStatusCode.OK "200"
                        })
            }

            testTask "/health/ready is a 503 problem with a trace ID when the database is down" {
                do!
                    withApi unreachable (fun client ->
                        task {
                            let! response = client.GetAsync "/health/ready"
                            do! expectProblem response HttpStatusCode.ServiceUnavailable
                        })
            }

            testTask "an unknown route is a 404 problem with a trace ID" {
                do!
                    withApi unreachable (fun client ->
                        task {
                            let! response = client.GetAsync "/nothing-here"
                            do! expectProblem response HttpStatusCode.NotFound
                        })
            }
        ]
