namespace Keygate.Lab;

/// <summary>Small, dependency-free console UI. No colors, no box drawing.</summary>
public static class Ui
{
    public static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', Math.Min(72, Math.Max(20, title.Length))));
    }

    public static void Field(string name, object? value) =>
        Console.WriteLine($"  {name,-16} {value}");

    public static void Info(string message) => Console.WriteLine($"  ..   {message}");

    public static void Ok(string message) => Console.WriteLine($"  OK   {message}");

    public static void Warn(string message) => Console.WriteLine($"  WARN {message}");

    public static void Fail(string message) => Console.WriteLine($"  FAIL {message}");

    public static void Bullet(string message) => Console.WriteLine($"       - {message}");
}
