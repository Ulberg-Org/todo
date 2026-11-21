# The tools for working on todo (D79): the same for developers (`nix develop`) and for Claude's sandbox, whose
# image adds `packages.tools` (platform repo, sandbox/Dockerfile). Everything else (kind, kubectl, helm)
# comes in the platform CLI's image.
{
  description = "todo: developer tools";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  outputs = { nixpkgs, ... }:
    let
      systems = [ "aarch64-darwin" "aarch64-linux" "x86_64-linux" ];
      forEach = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
      tools = pkgs: with pkgs; [
        docker-client # make up (the platform CLI runs as an image)
        cosign # checks the platform CLI image's signature
        gnumake
        git
        gh
        jq
      ]
      # .NET from nixpkgs on Linux (the sandbox); on macOS it would build Swift from source, so there the system's
      # .NET 11 is used (Homebrew).
      ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isLinux [ pkgs.dotnetCorePackages.sdk_11_0 ];
    in
    {
      devShells = forEach (pkgs: {
        default = pkgs.mkShell {
          packages = tools pkgs;
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        };
      });
      # The sandbox's image adds only what Docker's Claude image lacks (it has Docker, git, gh, make, jq): least
      # footprint, about 0.9 GB instead of 1.7.
      packages = forEach (pkgs: {
        tools = pkgs.buildEnv {
          name = "todo-sandbox-tools";
          paths = [ pkgs.cosign ] ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isLinux [ pkgs.dotnetCorePackages.sdk_11_0 ];
        };
      });
    };
}
