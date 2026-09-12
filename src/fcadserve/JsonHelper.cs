using System.Text.Json;
using System.Text.Json.Serialization;

namespace FcadServe;

public static class JsonHelper
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}