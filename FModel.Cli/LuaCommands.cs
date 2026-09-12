using System.Security.Cryptography;
using FModel.Cli.Lua;

namespace FModel.Cli;

internal sealed partial class GameSession
{
    private static object LuaLimits() => new { maxAssetBytes = LuaChunkReader.MaxFileBytes, maxFunctions = LuaChunkReader.MaxFunctions,
        maxInstructions = LuaChunkReader.MaxInstructions, maxDepth = LuaChunkReader.MaxDepth, maxNormalizedCharacters = 16 * 1024 * 1024,
        maxIdentityWork = 20000000,
        constantPreviewCharacters = 256, longValues = "bounded preview plus SHA256; never shortened for equality" };

    private static (LuaChunk Chunk, string Hash) ReadLua(CUE4Parse.FileProvider.Objects.GameFile file, string dialect)
    {
        if (file.Size < 0 || file.Size > LuaChunkReader.MaxFileBytes)
            throw new AnalysisException("LuaResourceLimit", "Selected file exceeds the 4 MiB Lua analysis limit.");
        var bytes = file.Read();
        return (LuaChunkReader.Read(bytes, dialect), Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public object LuaRead(CliOptions options)
    {
        var source = SelectContainer(options.Required("container")); var file = ExactFile(source, options.Required("asset"));
        var (chunk, hash) = ReadLua(file, options.Optional("dialect") ?? "auto");
        var document = new LuaDocument(chunk, options.Command == "lua-disasm" && options.Optional("function") == "$");
        var offset = options.Number("offset", 0, 0, int.MaxValue); var limit = options.Number("limit", 20, 1, 200);
        if (options.Command == "lua-functions")
        {
            var query = options.Optional("query") ?? "";
            var functions = document.Functions.Values.Where(f => f.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
            return new { asset = file.Path, container = Source(source), sha256 = hash, dialect = chunk.Dialect, total = functions.Length, offset, limit,
                functions = functions.Skip(offset).Take(limit).Select(f => new { id = f.Id, identity = f.Identity, parameters = f.Prototype.Parameters,
                    instructions = f.Instructions.Length, firstLine = f.Prototype.FirstLine, lastLine = f.Prototype.LastLine,
                    captures = f.Captures, identityKind = f.Identity.StartsWith("structural:") ? "structural-signature" : "named" }),
                scope = new { kind = "asset", function = (string?)null }, limits = LuaLimits() };
        }
        if (!document.Functions.TryGetValue(options.Required("function"), out var function))
            throw new AnalysisException("LuaFunctionNotFound", "Use an exact function id from lua-functions.");
        return new { asset = file.Path, container = Source(source), sha256 = hash, dialect = chunk.Dialect, function = function.Id,
            metadata = LuaFunctionDiff.Metadata(function), total = function.Instructions.Length, offset, limit,
            instructions = function.Instructions.Skip(offset).Take(limit), scope = new { kind = "function", function = function.Id,
                childFunctionsCompared = false, unresolvedChildIdentities = document.HasUnresolvedChildIdentities }, limits = LuaLimits() };
    }

    public object LuaDiff(CliOptions options)
    {
        var target = SelectContainer(options.Required("container")); var after = ExactFile(target, options.Required("asset"));
        var old = options.Optional("against") is { } against ? SelectContainer(against) : Previous(target, after.Path);
        var before = ExactFile(old, after.Path); var dialect = options.Optional("dialect") ?? "auto";
        var left = ReadLua(before, dialect); var right = ReadLua(after, dialect);
        var maxChanges = options.Number("max-changes", 100, 1, 1000); var maxWork = options.Number("max-work", 2000000, 1, 10000000);
        var rootOnly = options.Optional("function") == "$";
        var beforeDocument = new LuaDocument(left.Chunk, rootOnly); var afterDocument = new LuaDocument(right.Chunk, rootOnly);
        var diff = LuaFunctionDiff.Compare(beforeDocument, afterDocument, options.Optional("function"),
            options.Number("offset", 0, 0, int.MaxValue), options.Number("limit", 20, 1, 200), maxChanges, maxWork);
        return new { asset = after.Path, before = Source(old), after = Source(target), beforeSha256 = left.Hash, afterSha256 = right.Hash,
            beforeDialect = left.Chunk.Dialect, afterDialect = right.Chunk.Dialect, contentChanged = left.Hash != right.Hash,
            function = options.Optional("function"), scope = new { kind = options.Optional("function") is null ? "asset" : "function", function = options.Optional("function"),
                childFunctionsCompared = options.Optional("function") is null,
                unresolvedChildIdentities = beforeDocument.HasUnresolvedChildIdentities || afterDocument.HasUnresolvedChildIdentities },
            diff, maxChanges, maxWork, limits = LuaLimits() };
    }
}
