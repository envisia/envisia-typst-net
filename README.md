# Envisia.Typst

Compiles [Typst](https://typst.app/) markup to PDF from .NET, in process. The package bundles the Typst compiler
(0.15.1) as a native library, so there is no CLI to install and no process to spawn.

```sh
dotnet add package Envisia.Typst
```

Supported runtimes: `linux-x64`, `linux-arm64` (glibc 2.28 or newer), `osx-x64`, `osx-arm64`, `win-x64` and
`win-arm64`, on .NET 10. Each native library is about 35 MB. Publish with a runtime identifier
(`dotnet publish -r linux-x64`, or a container build) so only the matching one ends up in the output; a publish
without one carries all six.

## Usage

```csharp
var pdf = TypstCompiler.CompilePdf(
    new TypstCompileRequest
    {
        Markup = "#set text(font: \"Open Sans\")\n#text(\"Hallo\")",
        Fonts = [new TypstFont(openSansBytes)],
        Files = [new TypstFile("logo.svg", svgBytes)],
        Today = new DateOnly(2024, 5, 17),
    });
```

- `TypstCompiler.CompilePdf` returns the PDF bytes or throws `TypstCompileException`, which carries Typst's own
  diagnostics (with source positions and hints) in `Diagnostics`.
- Everything the document needs is passed in. The compiler reads no fonts from the machine, opens no files and
  makes no network calls, so a rendered document depends only on its inputs. No font is bundled either: pass at
  least one.
- `Today` feeds Typst's `datetime.today()`. Left unset that call fails to compile rather than silently reading a
  wall clock inside the renderer.
- A document is expected to be a fixed template that reads its data from a file handed in through `Files`, for
  example `#let d = json("data.json")`. A value read that way is data: Typst shows a string as text instead of
  parsing it, so untrusted input can never turn into executable markup. `Files` also serves `#import`, so a
  template can be split across several documents.
- `Creator` sets the application the PDF names as its creator (`/Creator` and XMP `CreatorTool`). Left unset,
  Typst names itself (`Typst 0.15.1`); an empty string leaves the entry out. Typst writes no `/Producer`, and
  typst-pdf offers no way to set one.
- `CompilePdf` blocks the calling thread while Typst compiles.

### Thread safety

`CompilePdf` can be called from any number of threads at once. The binding keeps no state between calls: every
call builds its own Typst world from the request's markup, fonts, files, date and creator, and nothing of it
outlives the call. A test compiles eight different documents that share a file name on twelve threads and checks
every PDF is byte for byte what the same request produces on its own.

Typst itself keeps three things process wide, which a binding cannot scope to one call:

- comemo's memoization cache. It is lock protected, and a cached result is only reused when every input Typst
  tracked is identical, so it can make a call faster but never hand it another call's data. The native layer
  clears it after every document; a call running at that moment loses cache hits, not correctness.
- The interner for file names. Each distinct name stays allocated for the life of the process, so use stable
  names such as `data.json` rather than a new name per call.
- rayon's global thread pool, which Typst lays out and exports on in parallel.

## Native layer

`src/Envisia.Typst/native/` is a Rust `cdylib` that implements Typst's `World` over the caller's fonts and files.

The C ABI is deliberately small:

| Symbol | Purpose |
| --- | --- |
| `envisia_typst_abi_version` | Version handshake, checked on every call. |
| `envisia_typst_compile_pdf` | Compiles markup into an `EvTypstResult`. |
| `envisia_typst_result_free` | Releases everything the result owns. |

Memory ownership: the caller owns the input buffers and only has to keep them alive for the duration of the call
(the managed side pins them). Everything in the result is allocated and freed by Rust; the managed side copies
the bytes out and always calls `envisia_typst_result_free`, including on the error paths. A Rust panic is caught
at the boundary and reported as a status rather than unwinding into the CLR.

Typst memoizes layout in comemo's process wide cache. The native layer clears it after every document
(`comemo::evict(0)`): each document here is rendered once, and a large one would otherwise stay resident until
later calls aged it out. The crate must depend on the comemo version Typst itself uses; with another one
`comemo::evict` clears a cache Typst never fills and the memory grows with every distinct document.
`same_comemo_as_typst` in `lib.rs` turns such a mismatch into a build error.

## Building

A local `dotnet build` or `dotnet test` compiles the native library for the host platform with
`cargo build --release` (through `Envisia.Typst.Native.targets`) and copies it next to the assembly.

- A Rust toolchain is required. `native/rust-toolchain.toml` pins the channel.
- `EnvisiaTypstSkipNativeBuild=true` reuses whatever is in `native/target`.
- `EnvisiaTypstLibraryPath=<path>` uses a prebuilt library instead of building one.

A cold `cargo build --release` compiles roughly 300 crates. Subsequent builds are incremental and cost well under
a second when nothing changed.

`dotnet pack` does not compile Rust. It takes one prebuilt library per runtime from
`EnvisiaTypstRuntimesDirectory` (laid out as `<rid>/<library>`) and fails when one is missing;
`EnvisiaTypstAllowMissingRuntimes=true` packs whatever is there, for a local test package.

## CI and releases

- `.github/workflows/build.yml` builds the native library on each platform's own runner (Linux inside the
  `manylinux_2_28` images, so the `.so` runs on glibc 2.28 and newer), packs them into one package and runs the
  tests on every platform against that package.
- `ci.yml` runs it for pushes to `main` and pull requests.
- `publish.yml` runs it for every pushed tag and pushes the package to nuget.org through trusted publishing
  (policy for `publish.yml`, environment `release`). The tag is the version: `v1.2.3`, `1.2.3` or a prerelease
  such as `v1.2.3-beta.1`; any other tag fails the workflow before anything is built.

To release, tag the commit and push the tag:

```sh
git tag v0.1.0
git push origin v0.1.0
```

## Licenses

Envisia.Typst is MIT licensed, see [LICENSE](LICENSE). The native library contains Typst (`Apache-2.0`) and about
290 Rust crates under permissive licenses; [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists them and ships in
the package. CI regenerates it with `cargo about` and fails when it is out of date. The test font is Open Sans under
the SIL Open Font License, see `tests/Envisia.Typst.Tests/Fonts/OFL.txt`.
