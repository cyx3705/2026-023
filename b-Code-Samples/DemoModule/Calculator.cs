namespace DemoModule;

internal static class Calculator
{
    public static int Add(int a, int b) => a + b;

    public static string Reverse(string text)
    {
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
