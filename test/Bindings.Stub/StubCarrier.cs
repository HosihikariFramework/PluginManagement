namespace Hosihikari.Bindings;

/// <summary>
/// 载体绑定的替身：真实的那份由星光产出（ADR-0021），这里只为验证
/// <b>共享层能不能同时吃下契约之外的第二个来源</b>（ADR-0019）。
/// </summary>
public interface ICarrierInfo
{
    string Describe();
}

public sealed class StubCarrier(string name) : ICarrierInfo
{
    public string Describe() => $"stub-carrier:{name}";
}

/// <summary>
/// 检验用：插件把上下文留在这里，模拟外部世界在卸载后仍攥着它。
/// 上下文少带一件插件侧的东西，插件 ALC 就多一个回收不了的根。
/// </summary>
public static class ContextSink
{
    public static object? Held { get; set; }
}
