/// What is running: for /version, telemetry and logs.
module Todo.Api.BuildInfo

/// The commit it was built from, when, the image tag (the commit SHA in CI, D54) and which instance
/// answered (D57: there are always at least two).
type BuildInfo =
    {
        commit: string
        builtAt: string
        imageTag: string
        instance: string
    }

/// Reads the build info from environment variables (set by the image build and the platform). A local run
/// has no build, so it says so.
let read (variable: string -> string option) (machineName: string) : BuildInfo =
    {
        commit = variable "BUILD_COMMIT" |> Option.defaultValue "local"
        builtAt = variable "BUILD_TIME" |> Option.defaultValue "local"
        imageTag = variable "IMAGE_TAG" |> Option.defaultValue "local"
        instance = variable "INSTANCE_NAME" |> Option.defaultValue machineName
    }
