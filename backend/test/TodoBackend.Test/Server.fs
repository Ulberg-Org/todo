/// Runs the real app (Program.create, as in production) on a free local port, against a real Postgres 18
/// started by Testcontainers with the same role script as local development.
module TodoBackend.Test.Server

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open DotNet.Testcontainers.Builders
open Testcontainers.PostgreSql

/// One Postgres for the whole test run.
let database: Lazy<Task<PostgreSqlContainer>> =
    lazy
        (task {
            let container =
                PostgreSqlBuilder("postgres:18")
                    .WithDatabase("todo")
                    .WithResourceMapping(
                        DirectoryPath.Of(Path.Combine(AppContext.BaseDirectory, "db", "init")),
                        DirectoryPath.Of("/docker-entrypoint-initdb.d/")
                    )
                    .Build()

            do! container.StartAsync()
            return container
        })

/// How the app connects: as its own role (todo_app), never as the superuser.
let appConnection (container: PostgreSqlContainer) =
    $"Host={container.Hostname};Port={container.GetMappedPublicPort(5432)};Database=todo;"
    + "Username=todo_app;Password=local-only-app"

/// A database that doesn't exist: nothing listens on port 1.
let unreachable =
    "Host=127.0.0.1;Port=1;Database=todo;Username=todo_app;Password=none;Timeout=2"

/// A port nothing listens on right now.
let private freePort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

/// The app's APIs as a client sees them, one port each (D73).
type Api =
    {
        /// What the gateway routes from outside.
        ``public``: HttpClient
        /// What only the platform reaches: health and version.
        management: HttpClient
    }

/// Starts the app with `connectionString` on two free ports, runs `test` against it over HTTP, and always stops
/// the app.
let withApi (connectionString: string) (test: Api -> Task<unit>) : Task<unit> =
    task {
        let publicPort, managementPort = freePort (), freePort ()

        let app =
            TodoBackend.Program.create
                [|
                    $"--Api:PublicPort={publicPort}"
                    $"--Api:ManagementPort={managementPort}"
                    $"--Database:ConnectionString={connectionString}"
                |]

        do! app.StartAsync()

        use publicClient =
            new HttpClient(BaseAddress = Uri $"http://127.0.0.1:{publicPort}")

        use managementClient =
            new HttpClient(BaseAddress = Uri $"http://127.0.0.1:{managementPort}")

        let client =
            {
                ``public`` = publicClient
                management = managementClient
            }

        let! outcome =
            task {
                try
                    do! test client
                    return None
                with ex ->
                    return Some(ExceptionDispatchInfo.Capture ex)
            }

        do! app.StopAsync()
        do! app.DisposeAsync()

        match outcome with
        | Some failure -> failure.Throw()
        | None -> ()
    }
