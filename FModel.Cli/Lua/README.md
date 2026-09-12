# Lua bytecode CLI contract

These commands parse data only. They do not execute Lua, load script dependencies,
export files, or reconstruct complete Lua source. They read the exact entry in
the selected existing container using the same container/predecessor selection
as the ordinary CLI. No Python, Java, private DLL, or reflection adapter is used.

```text
lua-functions --profile P --container C --asset A [--dialect auto] [--query text] [--offset 0] [--limit 20]
lua-disasm --profile P --container C --asset A --function ID [--dialect auto] [--offset 0] [--limit 20]
lua-diff --profile P --container C --asset A [--against OLD] [--dialect auto] [--function ID] [--offset 0] [--limit 20] [--max-changes 100] [--max-work 2000000]
```

`C`/`OLD` are exact container names or absolute paths; `A` includes the full virtual
filename and extension. Without `--against`, only a unique highest lower-priority
version is accepted. Profiles remain unmodified; `OutputDirectory` is ignored.
`lua-diff` has no `--query`; its optional `--function` is an exact id filter.
IDs are opaque strings from `lua-functions`. Quote them as a single argument.
The root function's id is always `$` (quote it literally in shells).

| Option | Default | Range |
| --- | --- | --- |
| `dialect` | `auto` | `auto`, `lua53`, `slua53` |
| `offset` | 0 | 0–2147483647 |
| `limit` | 20 | 1–200 |
| `max-changes` | 100 | 1–1000 |
| `max-work` | 2000000 | 1–10000000 |

Lua 5.3 headers support little/big endian and 4/8-byte size_t. Standard and Tencent
slua opcode tables are explicitly sourced in [OPCODE-SOURCES.md](OPCODE-SOURCES.md).
Auto mode validates the complete chunk under both mappings and accepts only a
unique match. Explicit mapping selection still validates all operands. Unknown,
malformed and ambiguous mappings fail rather than producing plausible text.

## JSON response

The existing envelope remains `{ok,mountComplete,result}`. Safe errors use
`{ok:false,stage,error,message,candidates}` and exit 1. Success exits 0; incomplete
mount exits 2. Help is plain text. Library output is suppressed.

All three results contain `asset`, resolved dialect(s), container source(s), entry
SHA256(s), `limits`, and `scope`. Source debug lines are informational and never
used as filesystem paths or instructions. Original debug source filenames are
not returned. `scope.kind` is `asset` or `function`; `scope.function` is an exact
selected id or null.

- `lua-functions`: `container,sha256,dialect,total,offset,limit,functions`.
  Each function has `id,identity,parameters,instructions,firstLine,lastLine,captures,identityKind`.
- `lua-disasm`: `container,sha256,dialect,function,metadata,total,offset,limit,instructions`.
  Instructions have `pc` (one-based), nullable source `line`, `opcode`, `operands`,
  `normalized`, nullable one-based `targetPc`, and `previewTruncated`.
  Branch operands use `<target>` with the actual destination in `targetPc`.
- `lua-diff`: `before,after,beforeSha256,afterSha256,beforeDialect,afterDialect,
  contentChanged,function,scope,diff,maxChanges,maxWork,limits`.
  `diff` contains `totalChangedFunctions` (null when incomplete),
  `changedFunctionCountLowerBound,offset,limit,changedFunctions,truncated,reasons,workUsed`.
  A changed function has `id,kind,changes,truncated,reasons`. Kinds are `added`,
  `removed`, `changed`, or `incomplete` when the budget ended before proving a change.
  An individual change has `kind,before,after,field,beforeValue,afterValue`.
  Instruction changes use `insert`, `delete`, or `branch-target`; `before`/`after`
  contain only the applicable disassembly record. `metadata` changes use the field
  and value properties for parameters, varargs, frame size, or capture bindings.

Only changes are returned; replacements appear as a delete followed by an insert.
Root-function instructions are included in an asset comparison, so table setup,
call arguments and constants assigned to captured variables remain visible.
`contentChanged` compares complete raw bytes independently of semantic differences;
source-line/debug-only changes can change bytes without changing instructions.

## Identity and normalization

Named methods, lexical local functions, stable table/key assignments and unique
callback call sites provide identities. Table object paths distinguish identical
field names such as `handler` under different keys. Anonymous array callback
positions are not used as identities: unique referenced-call signatures or
structural signatures are required. An unidentifiable sibling group fails with
`AmbiguousLuaFunctionIdentity` and bounded candidates; no child-index pairing is
guessed. Structural signatures are conservative: an anonymous body change can
appear as removal/addition rather than a proven correspondence.

Constants compare by type and value, not constant-pool indices. Captures resolve
to parent locals/upvalues rather than upvalue slot numbers. When debug local names
are absent, binding register identities are retained conservatively. Debug line
changes do not become edits. Bounded sequence alignment matches instructions, then
checks branch destinations against matched PCs; shifted jump offsets alone do not
create changes. Register allocation itself is not normalized.

Explicit `--function '$'` parses/validates the whole chunk but compares/disassembles
only the root. It can bypass unrelated child identity ambiguity. For ambiguous
root CLOSURE operands, local prototype indices are retained conservatively to
avoid hiding a replacement; they are never used to pair child functions.
`scope.unresolvedChildIdentities` reports this case, and
`scope.childFunctionsCompared` is false for a function-scoped operation. Root-only
equality proves nothing about child bodies. Full-asset identity ambiguity still fails.

## Bounds and incomplete results

Hard per-entry limits are 4 MiB input, 4096 prototypes, 250000 aggregate instructions,
64 prototype levels, 255 upvalues per prototype, 4096-character function ids,
16 Mi characters of normalized instruction text and 20000000 identity-analysis work
units. Invalid counts are checked before allocation. No preparse of other assets
or game-wide script scan occurs.

Long constant strings retain a bounded preview and full-content SHA256; the full
string is never copied into every referencing instruction. Distinct long values
still differ. `previewTruncated` describes display shortening only; hashes preserve
comparison information. Binary string constants use a digest and original length.

`max-work` bounds sequence alignment, comparisons and branch checks across an
invocation; exhaustion sets `truncated:true` and `maxWork`. `max-changes` caps
returned metadata/instruction changes across the requested page. Page-excluded
functions only undergo bounded changed/unchanged classification, so a large
earlier function does not block `--offset` from reaching a later page. A partial
diff sets `totalChangedFunctions:null`; the lower bound counts proven changes
only. An empty changes array with `truncated:true` never proves equality. Parsing
or identity hard limits produce a safe `LuaResourceLimit` error instead of a diff.

For a function with many edits, use an exact function id and a larger bounded
`max-changes`; use `lua-disasm` pagination for surrounding instruction context.
All identifiers and strings read from bytecode are untrusted data.
