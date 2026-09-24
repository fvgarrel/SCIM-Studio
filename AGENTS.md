# AGENTS.md

Working agreements for this repository. Read this before changing anything.

## The project

**SCIM Studio** - a desktop tool that manages users and groups on a SCIM 2.0
service provider the way an identity provider does, and checks the provider
against RFC 7643, RFC 7644 and the habits of Microsoft Entra ID and Okta.

```
src/ScimStudio.Core/        client, exchange log, dialects, checks, generator. No UI.
src/ScimStudio.DemoServer/  an in-memory SCIM server on the loopback interface
src/ScimStudio.App/         Avalonia 12, Fluent base, CommunityToolkit.Mvvm
tests/ScimStudio.Tests/     core and demo server, against a real server on a free port
tests/ScimStudio.App.Tests/ view models, catalogues, screenshots - headless, drawn by Skia
```

.NET 10 · Avalonia 12 · xunit v3 · no other runtime dependencies worth naming

```bash
dotnet run --project src/ScimStudio.App
dotnet build ScimStudio.slnx                               # warnings are errors
dotnet test ScimStudio.slnx
dotnet format ScimStudio.slnx --verify-no-changes --include src/ tests/
```

The trailing slashes after `--include` matter: without them the format check
matches no file and passes on anything.

## The rules that matter most

1. **The core knows no language.** Everything it tells a person is a
   `Message` - a catalogue key and its values - and the app translates it.
   English (`Localization/en.json`) is the source; every other catalogue
   mirrors its keys and placeholders, which a test holds. No German string
   outside `de.json`; code, comments, commits and docs in English.

2. **Every request goes through the recording handler.** The log is the
   product as much as the forms are. A request sent any other way is one the
   person cannot see, and an `ExchangeScope` names what each one was for.

3. **A dialect writes what its provider writes.** Before changing how Entra ID
   or Okta requests look, check the provider's documentation; a dialect that
   is "cleaner" than its provider tests nothing.

4. **A check says why.** A failing check carries a note with what came back,
   and the requests it sent. It creates only resources carrying its marker
   and removes them whatever happens, cancellation included. What RFC 7644
   leaves optional and the server does not offer - by its configuration or
   by how it answers - is *unsupported*, not failed, and so is every check
   that stands on it. When in doubt, unsupported.

5. **Never define the same thing twice.** A colour, a label, a mapping from a
   dialect to its name: once, and referenced. The palette is in
   `Styles/Tokens.axaml`, the icons in `Controls/Icons.cs`.

## Code style

Most of it is enforced: `.editorconfig` holds the rules, `dotnet format`
applies them, and the build treats every warning as an error.

- Braces open on the line that opens the block; `} else {` on one line.
- `=>` only for a getter that fits one line; methods always have a body.
- Constants `UPPER_SNAKE`, private fields `_camelCase`, private static
  readonly fields `PascalCase`.
- Lines up to 150 columns, checked by a test because `dotnet format` does
  not wrap.
- Comments say why, never what, in two or three lines. XML docs on the
  member they describe; a documented member describes every parameter.

## Looking at it

A type check says nothing about a layout. The app tests draw every page in
both themes and both languages into `artifacts/screenshots`, and write what
an export and a copy of a check hold into `artifacts/reports`; after a
visual change, or one to a report, look at them. `docs/screenshots` holds
the copies the README shows, replaced by hand.

## Commits

Conventional Commits - `type(scope): subject`, English, imperative, short;
the body says why. One commit per completed change, made without being
asked. No attribution trailers: the author field says who is responsible.
Scopes in use: `core`, `demo`, `app`, `ci`.

The commits are the release notes (`packaging/release-notes.sh`): a `feat`,
`fix` or `perf` subject is a line in them and the first paragraph of its body
the note beneath, so write both for someone using the app. `!` or a
`BREAKING CHANGE:` footer puts a commit under *Breaking changes* with the
footer as what to do; every other type is folded away as maintenance.

## Versions

The version is the latest `v*` tag, read by MinVer at build time - nowhere is
it written down. A release is a tag: `v1.2.0` on a commit builds 1.2.0 and
publishes it. Workflows check out with `fetch-depth: 0`; a shallow clone has
no tags and builds 0.0.0-alpha.0.

On macOS the download is `SCIM Studio.app` in a disk image, put together by
`packaging/macos/bundle.sh` from the publish folder, `Info.plist` and the
icns the icon test renders. It needs a Mac to run; start the release workflow
by hand to try a change to it without releasing.
