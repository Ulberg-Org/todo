# F# style

These rules apply to **every F# file** in `Ulberg-Org/todo` and `Ulberg-Org/platform`: application code, Pulumi stacks, scripts and tests. There are no per-file exceptions. This file is identical in both repos (platform CI checks that), so change it in both at once.

Most of this is standard F#, explained well by Scott Wlaschin's *F# for Fun and Profit*. The reading list at the end links the articles each rule comes from. `reference/` shows the same patterns in a real codebase.

## 1. Program shape: input → domain → action → report

Every program, handler, job and stack is a pipeline in four steps, in this order:

1. **Input data.** Read files, requests, config or environment as DTOs. This is the only place untrusted or untyped data enters.
2. **Domain model.** Parse the DTOs into domain types and compute what should happen, with **pure functions** only. All decisions are made here, and this is what the unit tests cover.
3. **Action.** Perform the effects: write to the database, call an API, register Pulumi resources. Action code makes no decisions; it turns domain values into effects and returns what it did.
4. **Report.** Log results, failures and alerts from the values the earlier steps returned. Failures are values (`Result`), collected and reported in one place.

Effects live only at the edges (steps 1, 3 and 4), in functions whose name and signature make that obvious (`deploy`, `read…`, `save…`). Example: `platform/stacks/access/Program.fs` reads `people.yaml` and config, parses them into `Access`, builds a pure `Desired.Plan`, deploys the plan, and logs either the errors or a summary.

## 2. No mutation and no hidden side effects (mutation is CI-enforced)

- No `let mutable`, no `<-` assignment, no setting properties on an object after creating it. Build values in one expression (record construction, `List.map`, folds, object initializers in the constructor call).
- No `|> ignore` to throw away a result. If a function returns something, use it or return it. If it returns nothing useful, it shouldn't be called in the middle of pure code. There are two exceptions: a fluent builder (ASP.NET, OpenTelemetry) that returns itself inside a configuration callback, used sparingly; and, in a Pulumi program's action step, a resource nothing needs a handle to (constructing it registers it), so `Command(…) |> ignore`. Never a made-up output or binding just to avoid `ignore`.
- A function is either **pure** (same input, same output, no effects) or an **action** at the edge. A pure-looking function must never register, write, send or log.
- Repeating an action with the same input must give the same result (idempotent). Pure planning plus declarative actions (Pulumi, `INSERT … ON CONFLICT`, PUT) gives that for free.
- Collections are F# immutable ones (`list`, `Map`, `Set`). Arrays and .NET collections appear only at library boundaries.

## 3. No `null`: use `Option` and `Result` (CI-enforced)

- Absence is `Option`. Failure is `Result` (or `Validation` when you want every error at once). `null` never appears in our types or our code.
- Libraries that return `null` are wrapped **at the call site** with `Option.ofObj` / `Option.ofNullable`, and nothing nullable goes further than that line. Leave a comment there saying so.
- DTO fields that may be missing are `option`, not nullable. System.Text.Json maps F# records, options, lists and maps natively.

## 4. Parse, don't validate: DTO → domain

- **DTOs** are plain records that mirror the wire or file format exactly. Every field that can be missing is an `option`, and there's no logic in them.
- **Domain types** can only hold valid values. Parsing a DTO either returns a domain value or the reasons it can't.
- Combine field parsers applicatively so every problem is reported at once: `Person.create <!^> PersonKey.parse key <*^> GitHubHandle.parse github …` or `validation { let! a = … and! b = … }`.
- Unknown input is an error, not ignored. For example, deserializers reject unmapped members.

## 5. Types

- **Single-case unions with a private constructor** for every domain primitive (`PersonKey`, `EntraObjectId`, `Email`, …), each with a companion module of the same name exposing `parse`, `value` (or `string`) and, where useful, `create`.
- **Discriminated unions** for closed sets (`GroupName`, `CaState`), never strings. Use `[<RequireQualifiedAccess>]` when case names would clash with other types.
- Make illegal states unrepresentable: if a combination isn't allowed, the types shouldn't allow it.
- Record fields are camelCase; types, union cases and modules are PascalCase.

## 6. Errors

- Expected failures are `Result`/`Validation` values with messages that say where (`people.bob: 'entra': … is not an object ID`).
- Exceptions are only for bugs and for the outermost edge (e.g. failing a Pulumi run once, after reporting). Catch library exceptions at the boundary and turn them into `Result`.
- Use **FsToolkit.ErrorHandling** (`result {}`, `validation {}`, `taskResult {}`, `List.traverseValidationA`, `Result.requireSome`, …). Don't write your own builders or traversal helpers.

## 7. Modules and libraries

- Prefer established libraries over hand-written helpers (FsToolkit.ErrorHandling, System.Text.Json's built-in F# support, YamlDotNet). Check first; write a helper only when nothing fits.
- Generic, reusable helpers go in their own module (e.g. `Parse.fs`), not as a pile of `private` functions in a domain file. Domain files contain domain rules.
- Order files top-down in the `.fsproj`: primitives → domain → planning → actions → program.

## 8. Readable code: name the parts first, then build

A function first names what it needs, then puts the result together. The final expression (a record, a manifest, a request, a `validation {}` block) reads as a list of names, with no decisions inside it.

- **No `if`/`match` nested inside a construction.** No branching inside a record, a list literal or an argument. Lift each decision into a named value or a small function earlier in the function (or above it), and use the name.
- **Decide a choice once.** When several parts depend on the same choice (a mode, a role, an environment), match on it once into a record of what differs, and build from that record. Example: `Runtime` in `platform/renderer/…/Render.fs`.
- **Optional parts are `Option`, not `if … then [x] else []`.** Something that's only there sometimes is a `'T option`. Put it in a list with `Option.toList`, or use a helper that leaves out the missing ones (`Manifest.optional`).
- **Flat `validation {}` blocks.** One line per field: `and! port = Field.port d.port`. The rule logic goes in a private `Field` module next to it, and generic checks go in `Parse`.
- **Deep nesting is a smell.** Past three or four levels, give the inner parts names.

## 9. Native F# first (the lambda rules are CI-enforced)

Use what F# itself offers before constructs every language has. `reference/` does this throughout.

- **Turn values into `Option` and `Result` with pipelines, not `if`.** A bool that decides success is a `Result`: `Result.requireTrue`, then `Result.map`. An option that must be there is `Result.requireSome`. A missing value gets a default with `Option.map` and `Option.defaultValue`. A choice between cases is a union and a `match`; a loop is `List.map`, `List.choose` or a fold; a sequence of steps is a pipeline or a computation expression.

  ```fsharp
  // Not
  if found.Success then Ok { location = found.Groups[1].Value } else Error "location is missing"
  // But
  found.Success
  |> Result.requireTrue "location is missing"
  |> Result.map (fun _ -> { location = found.Groups[1].Value })
  ```

- **Lambdas as short as they can be:**
  - `_.Name` (F# 8) when a lambda only reads a property, calls a method or indexes its argument: `List.map _.name`, `.Apply(_.PrincipalId)`. Not `fun x -> x.name`.
  - `fun _ ->` when the argument isn't used, including `()`. Not `fun () ->`.
  - A function by name instead of a lambda that only calls it: `List.map Email.value`, `Deployment.RunAsync run`.

## 10. Formatting and warnings (CI-enforced)

- Fantomas formats everything (`dotnet fantomas --check`). `TreatWarningsAsErrors` and nullness checking are on.
- No warning suppressions without a comment saying why, and no `TODO` hacks.

## Reading list (F# for Fun and Profit, and Microsoft for §9)

- [Designing with types](https://fsharpforfunandprofit.com/series/designing-with-types/): the series behind §4–5, especially [single case union types](https://fsharpforfunandprofit.com/posts/designing-with-types-single-case-dus/) and [making illegal states unrepresentable](https://fsharpforfunandprofit.com/posts/designing-with-types-making-illegal-states-unrepresentable/).
- [The Option type](https://fsharpforfunandprofit.com/posts/the-option-type/): why F# uses `Option` instead of `null` (§3).
- [Railway oriented programming](https://fsharpforfunandprofit.com/rop/): `Result`-based error handling and keeping effects at the edges (§1, §6).
- [Using the core functions in practice](https://fsharpforfunandprofit.com/posts/elevated-world-3/): applicative validation that collects every error (§4).
- [Lambda expressions](https://learn.microsoft.com/dotnet/fsharp/language-reference/functions/lambda-expressions-the-fun-keyword) and [Announcing F# 8](https://devblogs.microsoft.com/dotnet/announcing-fsharp-8/) (Microsoft): the `_.Name` shorthand (§9).
- [Serializing your domain model](https://fsharpforfunandprofit.com/posts/serializing-your-domain-model/): DTOs at the boundary, domain types inside (§4).
