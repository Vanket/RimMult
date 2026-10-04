# Notes for AI assistants

Start with `docs/HANDOFF.md` (state of the project, what is verified in game and what isn't, architecture,
pitfalls, next steps); `docs/DESIGN.md` has the full design.

* Build: `dotnet build`; tests: `dotnet test` (all must pass; warnings are errors).
* RimWorld reference assemblies have no method bodies: check signatures, don't guess them.
* Changing a packet format → bump `ProtocolInfo.Version` and add a round-trip test.
* New UI text → add the key to both `mod/Languages/Russian/Keyed/RimMult.xml` and `.../English/...`.
* Code comments in English, docs and in-game text in Russian/English; the owner speaks Russian.
* Keep `mod/About/PublishedFileId.txt` (Steam Workshop item 3813029532).
