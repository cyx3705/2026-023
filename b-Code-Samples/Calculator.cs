namespace DemoModule;

public class Calculator
{
    /// <summary>两个整数相加,返回和</summary>
    /// <param name="a">第一个加数</param>
    /// <param name="b">第二个加数</param>
    public int Add(int a, int b) => a + b;

    /// <summary>向指定的人问好(演示异步方法与默认参数)</summary>
    /// <param name="name">要问候的名字,缺省为 World</param>
    public async Task<string> SayHello(string name = "World")
    {
        await Task.Delay(10);
        return $"Hello {name}!";
    }

    /// <summary>返回当前时间和机器名(演示静态方法与对象返回值的 JSON 回显)</summary>
    public static object Now() => new { time = DateTime.Now, machine = Environment.MachineName };

    /// <summary>把字符串反转</summary>
    /// <param name="text">要反转的字符串</param>
    public string Reverse(string text) => new(text.Reverse().ToArray());
}
