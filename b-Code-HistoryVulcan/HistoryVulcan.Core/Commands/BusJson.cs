using System.Text.Json;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 宿主指令的 Data 一律交出 JSON（6.0.0，DEC-071，判据 G4）。
/// </summary>
/// <remarks>
/// 5.9.0 之前 <c>vulcan.module.list</c>、<c>vulcan.command.list/show</c> 交出的是宿主内部的 C# 记录，
/// 消费方只能引用宿主实现程序集再强转，宿主一改记录就连带改模块。现在交出 <see cref="JsonElement"/>：
/// 契约是写在模块开发手册「宿主指令的 Data」里的 JSON 形状（驼峰字段名），字段只增不减，宿主内部类怎么改都不影响消费方。
/// </remarks>
internal static class BusJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>按契约序列化（驼峰字段名）并交出独立的 JSON 值。</summary>
    public static JsonElement ToElement(object? value)
        => value is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(value, Options);
}
