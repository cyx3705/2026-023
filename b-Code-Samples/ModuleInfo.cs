using BaseVariable;

namespace DemoModule;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "DemoModule";
    public override string Description => "示例模块:演示精准暴露 Calculator 类(验收 A3-1~A3-4 载体)";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";
    public override bool Open => false;
    public override Type? MainClassType => typeof(Calculator);
}
