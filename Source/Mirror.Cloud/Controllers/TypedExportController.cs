using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.Utils;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Mirror.Cloud.Controllers;

public partial class CloudApiController
{
    // Opt-in: the normal export consumed by Reflection retains its format.
    // FInstancedStruct's ordinary converter flattens the payload and loses the
    // concrete UE type, which native wildcard property lookups compare exactly.
    [HttpGet("export/typed")]
    public ActionResult GetTypedExport(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("No asset supplied");
        path = path.SubstringBefore('.');
        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found || !profile.Provider.TryLoadPackage(path, out var package)) return NotFoundResponse;

        return new ContentResult
        {
            Content = JsonConvert.SerializeObject(new { exports = package.GetExports().ToArray() },
                new JsonSerializerSettings { ContractResolver = new InstancedStructTypeResolver() }),
            ContentType = "application/json",
            StatusCode = 200
        };
    }

    private sealed class InstancedStructTypeResolver : DefaultContractResolver
    {
        protected override JsonContract CreateContract(Type objectType)
        {
            var contract = base.CreateContract(objectType);
            if (objectType == typeof(FInstancedStruct)) contract.Converter = new TypedInstancedStructConverter();
            return contract;
        }
    }

    private sealed class TypedInstancedStructConverter : JsonConverter<FInstancedStruct>
    {
        public override void WriteJson(JsonWriter writer, FInstancedStruct? value, JsonSerializer serializer)
        {
            if (value?.ScriptStruct is not { } script) { writer.WriteNull(); return; }
            var payload = JToken.FromObject(script.StructType, serializer);
            var result = payload as JObject ?? new JObject { ["Value"] = payload };
            result.AddFirst(new JProperty("$struct", script.StructName));
            result.WriteTo(writer);
        }

        public override FInstancedStruct ReadJson(JsonReader reader, Type objectType, FInstancedStruct? existingValue,
            bool hasExistingValue, JsonSerializer serializer) => throw new NotSupportedException();
    }
}
