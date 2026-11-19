/// The database connection (Npgsql 10 with Npgsql.FSharp on top). Phase 1 only checks that it answers.
module Todo.Api.Database

open System.Threading
open System.Threading.Tasks
open Npgsql
open Npgsql.FSharp

/// Whether the database answers a trivial query as the app's own role. An unreachable database is an
/// expected failure, so the exception becomes a Result here, at the library boundary.
let ping (source: NpgsqlDataSource) (cancellation: CancellationToken) : Task<Result<unit, string>> =
    task {
        try
            let! _ =
                Sql.fromDataSource source
                |> Sql.cancellationToken cancellation
                |> Sql.query "select 1"
                |> Sql.executeRowAsync (fun read -> read.int "?column?")

            return Ok()
        with ex ->
            return Error ex.Message
    }
