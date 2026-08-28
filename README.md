# RDR2 Save Research MVP

An offline, public starting point for studying RDR2 PC Story Mode (`SRDR...`)
save containers. It is intentionally a research tool—not a save editor and not
a general-purpose save merger.

## What it does

- verifies an exact decrypt/re-encrypt container round trip;
- indexes `RSAV`, `CHKS`, `PSIN`, `PMAP`, `PSCH`, and `PSIG` layout evidence;
- compares two save copies without guessing what changed bytes mean;
- discovers self-describing PSO frames, blocks, structures, and known hashes;
- statically extracts confidence-tagged labels/hashes from an editor resource
  blob without loading an assembly or deserializing its object graph;
- statically indexes NativeDB-style `natives.h` declarations as a separate,
  provenance-hashed catalog without compiling or invoking them;
- generates controlled-delta reports for one isolated game/editor change;
- produces a new throwaway test clone only when explicitly confirmed.

## Deliberate limits

This project does **not** expose general save mutation or merging commands.
RDR2 saves contain private state such as money, inventory, owned weapons,
horse/bonding, health, and unknown allocation metadata. A byte difference is
evidence, not proof that a value is a mission or unlock flag. Do all research
on copies and keep the game closed when examining a save.

Some experimental writer classes remain in source as documented implementation
history. They are not wired into the CLI. Do not promote one into a tool until
you have a one-change control, a schema-backed field location, container
round-trip verification, and a throwaway in-game load test.

## Prerequisites

- .NET SDK 10.0.203 or compatible latest patch
- A copy of a PC Story Mode save. Never point a command at your only save.

## Quick start

```powershell
dotnet build .\Rdr2SaveResearch.slnx -c Release

# Prove the encrypted SRDR container survives the codec unchanged.
dotnet run --project .\src\Rdr2SaveResearch -c Release -- verify --save .\copies\SRDR30000

# Write a new, read-only JSON report.
dotnet run --project .\src\Rdr2SaveResearch -c Release -- inspect `
  --save .\copies\SRDR30000 --output .\reports\slot-report.json

# Compare two copies taken before and after exactly one controlled change.
dotnet run --project .\src\Rdr2SaveResearch -c Release -- diff `
  --before .\copies\before.sav --after .\copies\after.sav `
  --output .\reports\delta.json

# Index a locally supplied natives.h as text only. This does not execute it.
dotnet run --project .\src\Rdr2SaveResearch -c Release -- extract-native-catalog `
  --input $env:USERPROFILE\Downloads\natives.h --output .\reports\native-catalog.json

# Use every native declaration in that catalog when inspecting a save copy.
dotnet run --project .\src\Rdr2SaveResearch -c Release -- inspect `
  --save .\copies\SRDR30000 --native-catalog .\reports\native-catalog.json `
  --output .\reports\slot-report.json
```

Run `dotnet run --project .\src\Rdr2SaveResearch -- help` for every command.
Commands refuse to overwrite report output. `create-test-clone` is the only
command that writes a save; it creates a new output file only and requires the
literal confirmation `CREATE_TEST_CLONE`.

## Suggested research method

1. Make a copy at a stable title-screen save point.
2. Change exactly one known thing—e.g. one recipe, medal, or shop condition.
3. Return to the title screen and copy the same slot again.
4. Verify both copies, then run `diff` and `analyze-control`.
5. Treat unknown or structural candidates as evidence only.
6. Validate any future writer against a disposable slot and preserve the
   original file byte-for-byte.

## Scope and credit

The codec is based on independently observed PC save-container behavior. This
repository intentionally does not include Rockstar game assets, save files,
save-editor binaries, or decrypted proprietary data. RDR2 and Rockstar are
trademarks of their respective owners; this is an unaffiliated research project.

Released under the [MIT License](LICENSE).
