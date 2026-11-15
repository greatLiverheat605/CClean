using System.Buffers;
using System.Text;
using System.Text.Json;
using CClean.Core.Files;

namespace CClean.Core.Cleanup;

// This is an audit format, never a way to restore an executable approval.
internal static class CleanupManifestCodec
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private const string Format = "cclean-cleanup-manifest";
    private sealed record DecisionKey(ProtectionDecision Decision, string? RootIdentity);

    internal static string Encode(CleanupPlan plan)
    {
        var scopes = new List<RuleScope>(); var scopeIds = new Dictionary<RuleScope, int>();
        var decisions = new List<DecisionKey>(); var decisionIds = new Dictionary<DecisionKey, int>();
        foreach (var item in plan.Items)
        {
            if (item.Decision.Scope is { } scope && !scopeIds.ContainsKey(scope))
            { scopeIds.Add(scope, scopes.Count); scopes.Add(scope); }
            var key = new DecisionKey(item.Decision, item.RootIdentity);
            if (!decisionIds.ContainsKey(key)) { decisionIds.Add(key, decisions.Count); decisions.Add(key); }
        }
        var buffer = new ArrayBufferWriter<byte>(Math.Max(256, Math.Min(plan.Items.Count, MaximumBytes / 350) * 350));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("Format", Format);
            writer.WriteNumber("Version", plan.Contract == CleanupContract.StrictActivity ? 1 : plan.Contract == CleanupContract.ObjectReviewV1 ? 2 : 3);
            writer.WriteString("Id", plan.Id); writer.WriteString("Created", plan.Created);
            if (plan.Contract != CleanupContract.StrictActivity) writer.WriteString("Contract", plan.Contract.ToString());
            writer.WriteString("ContextFingerprint", plan.ContextFingerprint); writer.WriteBoolean("Truncated", plan.Truncated);
            writer.WriteStartArray("Scopes");
            foreach (var scope in scopes) { JsonSerializer.Serialize(writer, scope); CheckSize(); }
            writer.WriteEndArray();
            writer.WriteStartArray("Decisions");
            foreach (var entry in decisions)
            {
                writer.WriteStartArray(); writer.WriteBooleanValue(entry.Decision.Eligible);
                writer.WriteStringValue(entry.Decision.Category); writer.WriteStringValue(entry.Decision.Reason);
                if (entry.Decision.Scope is { } scope) writer.WriteNumberValue(scopeIds[scope]); else writer.WriteNullValue();
                writer.WriteStringValue(entry.RootIdentity); writer.WriteEndArray();
                CheckSize();
            }
            writer.WriteEndArray(); writer.WriteStartArray("Items");
            foreach (var item in plan.Items)
            {
                var file = item.File;
                writer.WriteStartArray(); writer.WriteStringValue(file.Path); writer.WriteStringValue(file.Identity);
                Number(file.LogicalBytes); Number(file.AllocatedBytes); writer.WriteNumberValue(file.LinkCount);
                writer.WriteNumberValue((int)file.Attributes); writer.WriteNumberValue(file.ModifiedTime); writer.WriteNumberValue(file.ChangeTime);
                writer.WriteBooleanValue(file.StreamsComplete); writer.WriteBooleanValue(file.HasNamedStreams); writer.WriteStringValue(file.Exclusion);
                writer.WriteNumberValue(decisionIds[new(item.Decision, item.RootIdentity)]); writer.WriteEndArray();
                CheckSize();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); CheckSize();
            void Number(long? value) { if (value is long number) writer.WriteNumberValue(number); else writer.WriteNullValue(); }
            void CheckSize()
            {
                writer.Flush();
                if (buffer.WrittenCount > MaximumBytes) throw new IOException("审计清单超过 8 MiB，请缩小选择。");
            }
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static IReadOnlyList<CleanupItem> Decode(string json, string run, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new IOException("审计清单超出读取上限。");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var compact = root.TryGetProperty("Format", out var format);
            if (compact)
            {
                Shape(root, ["Format", "Version", "Id", "Created", "ContextFingerprint", "Truncated", "Scopes", "Decisions", "Items"], ["Contract"]);
                var version = root.GetProperty("Version").GetInt32();
                if (format.GetString() != Format || version is not (1 or 2 or 3)) throw Bad("清单格式或版本不支持");
                var hasContract = root.TryGetProperty("Contract", out var contract);
                if (version == 2 && (!hasContract || contract.GetString() != nameof(CleanupContract.ObjectReviewV1))) throw Bad("对象级契约缺失或不支持");
                if (version == 3 && (!hasContract || contract.GetString() != nameof(CleanupContract.SegmentReviewResearch))) throw Bad("研究契约缺失或不支持");
                // Also read the initial isolated v1+Contract experiment; neither format restores approval.
                if (hasContract && contract.GetString() is not (nameof(CleanupContract.StrictActivity) or nameof(CleanupContract.ObjectReviewV1) or nameof(CleanupContract.SegmentReviewResearch))) throw Bad("契约不支持");
                if (version != 3 && hasContract && contract.GetString() == nameof(CleanupContract.SegmentReviewResearch)) throw Bad("研究契约版本不匹配");
                _ = root.GetProperty("Created").GetDateTimeOffset();
                _ = Text(root.GetProperty("ContextFingerprint")); _ = root.GetProperty("Truncated").GetBoolean();
            }
            else
            {
                Shape(root, ["Items"], ["Id", "Created", "Expires", "ContextFingerprint", "Truncated", "EligibleCount", "EstimatedBytes", "Contract"]);
                if (root.TryGetProperty("Contract", out var contract) && contract.GetInt32() is not (0 or 1)) throw Bad("契约不支持");
                if (root.TryGetProperty("Created", out var created)) _ = created.GetDateTimeOffset();
                if (root.TryGetProperty("Expires", out var expires)) _ = expires.GetDateTimeOffset();
                if (root.TryGetProperty("ContextFingerprint", out var fingerprint)) _ = Text(fingerprint);
                if (root.TryGetProperty("Truncated", out var truncated)) _ = truncated.GetBoolean();
            }
            if (root.TryGetProperty("Id", out var id) && (!Guid.TryParse(id.GetString(), out var parsed) || parsed.ToString() != run))
                throw Bad("清单与任务身份不一致");
            var items = root.GetProperty("Items");
            if (items.ValueKind != JsonValueKind.Array) throw Bad("清单对象集合无效");
            var scopes = new List<RuleScope>(); var decisions = new List<DecisionKey>();
            if (compact)
            {
                foreach (var scope in root.GetProperty("Scopes").EnumerateArray())
                { cancellation.ThrowIfCancellationRequested(); scopes.Add(ReadScope(scope)); }
                foreach (var decision in root.GetProperty("Decisions").EnumerateArray())
                {
                    cancellation.ThrowIfCancellationRequested(); Array(decision, 5);
                    var scope = decision[3].ValueKind == JsonValueKind.Null ? null : At(scopes, decision[3].GetInt32());
                    decisions.Add(new(new(decision[0].GetBoolean(), Text(decision[1]), Text(decision[2]), scope), NullableText(decision[4])));
                }
            }
            var decoded = new List<CleanupItem>(Math.Min(1000, items.GetArrayLength()));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                CleanupItem value;
                if (compact)
                {
                    Array(item, 12); var decision = At(decisions, item[11].GetInt32());
                    value = new(new(Text(item[0]), NullableText(item[1]), NullableNumber(item[2]), NullableNumber(item[3]), item[4].GetUInt32(),
                        (FileAttributes)item[5].GetInt32(), item[6].GetInt64(), item[7].GetInt64(), item[8].GetBoolean(), item[9].GetBoolean(), NullableText(item[10])),
                        decision.Decision, decision.RootIdentity);
                }
                else
                {
                    Shape(item, ["File", "Decision", "RootIdentity"], ["Path", "Status", "Reason", "Estimate"]);
                    var file = ReadFile(item.GetProperty("File"));
                    var decision = item.GetProperty("Decision"); Shape(decision, ["Eligible", "Category", "Reason", "Scope"]);
                    value = new(file, new(decision.GetProperty("Eligible").GetBoolean(), Text(decision.GetProperty("Category")), Text(decision.GetProperty("Reason")),
                        decision.GetProperty("Scope").ValueKind == JsonValueKind.Null ? null : ReadScope(decision.GetProperty("Scope"))), NullableText(item.GetProperty("RootIdentity")));
                    if (item.TryGetProperty("Path", out var path) && path.GetString() != file.Path) throw Bad("清单路径字段冲突");
                }
                if (!Path.IsPathFullyQualified(value.Path) || value.File.LogicalBytes < 0 || value.File.AllocatedBytes < 0 || !paths.Add(value.Path))
                    throw Bad("清单路径、大小或重复对象无效");
                if (value.Decision.Eligible && (string.IsNullOrWhiteSpace(value.File.Identity) || string.IsNullOrWhiteSpace(value.RootIdentity) || value.Decision.Scope is null))
                    throw Bad("候选缺少对象或批准根身份");
                decoded.Add(value);
            }
            if (!compact && root.TryGetProperty("EligibleCount", out var eligible) && eligible.GetInt32() != decoded.Count(item => item.Decision.Eligible))
                throw Bad("旧清单候选计数不一致");
            if (!compact && root.TryGetProperty("EstimatedBytes", out var estimated) && estimated.GetInt64() != decoded.Where(item => item.Decision.Eligible).Sum(item => item.File.AllocatedBytes ?? 0))
                throw Bad("旧清单大小汇总不一致");
            return decoded.AsReadOnly();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or KeyNotFoundException)
        { throw new IOException("审计清单损坏或格式不支持，不能作为空结果或旧批准继续处理。", error); }
    }

    private static RuleScope ReadScope(JsonElement scope)
    {
        Shape(scope, ["Root", "RuleId", "RuleVersion", "ToolVersion", "Enabled", "Exclusive"]);
        var root = Text(scope.GetProperty("Root"));
        if (!Path.IsPathFullyQualified(root)) throw Bad("规则根路径无效");
        return new(root, Text(scope.GetProperty("RuleId")), scope.GetProperty("RuleVersion").GetInt32(), Text(scope.GetProperty("ToolVersion")),
            scope.GetProperty("Enabled").GetBoolean(), scope.GetProperty("Exclusive").GetBoolean());
    }
    private static FileSnapshot ReadFile(JsonElement file)
    {
        Shape(file, ["Path", "Identity", "LogicalBytes", "AllocatedBytes", "LinkCount", "Attributes", "ModifiedTime", "ChangeTime", "StreamsComplete", "HasNamedStreams", "Exclusion"], ["IsOrdinaryFile"]);
        return new(Text(file.GetProperty("Path")), NullableText(file.GetProperty("Identity")), NullableNumber(file.GetProperty("LogicalBytes")), NullableNumber(file.GetProperty("AllocatedBytes")),
            file.GetProperty("LinkCount").GetUInt32(), (FileAttributes)file.GetProperty("Attributes").GetInt32(), file.GetProperty("ModifiedTime").GetInt64(), file.GetProperty("ChangeTime").GetInt64(),
            file.GetProperty("StreamsComplete").GetBoolean(), file.GetProperty("HasNamedStreams").GetBoolean(), NullableText(file.GetProperty("Exclusion")));
    }
    private static void Shape(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Bad("对象格式无效");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !required.Contains(property.Name) && optional?.Contains(property.Name) != true) throw Bad("未知或重复字段");
        if (required.Any(name => !names.Contains(name))) throw Bad("缺少必需字段");
    }
    private static void Array(JsonElement value, int count)
    { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw Bad("清单行长度无效"); }
    private static T At<T>(IReadOnlyList<T> values, int index) => index >= 0 && index < values.Count ? values[index] : throw Bad("清单引用索引越界");
    private static string Text(JsonElement value) => value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : throw Bad("文本字段无效");
    private static string? NullableText(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString() ?? throw Bad("文本字段无效");
    private static long? NullableNumber(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    private static IOException Bad(string reason) => new("审计清单无效：" + reason + "。");
}
