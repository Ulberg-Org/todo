# The tools for working on todo (D79): the same for developers (`nix develop`) and for Claude's sandbox, which is
# built from this. Everything else (kind, kubectl, helm) comes in the platform CLI's image.
{
  description = "todo: developer tools";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  outputs = { nixpkgs, ... }:
    let
      systems = [ "aarch64-darwin" "aarch64-linux" "x86_64-linux" ];
      forEach = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      devShells = forEach (pkgs: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            docker-client # make up (the platform CLI runs as an image)
            cosign # checks the platform CLI image's signature
            gnumake
            git
            gh
            jq
          ]
          # .NET from nixpkgs on Linux (the sandbox, CI-like machines); on macOS it would build Swift from source,
          # so there the system's .NET 11 is used (Homebrew).
          ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isLinux [ pkgs.dotnetCorePackages.sdk_11_0 ];
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        };
      });
    };
}
