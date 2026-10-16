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
