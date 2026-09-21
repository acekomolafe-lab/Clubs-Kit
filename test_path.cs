using System;
using System.IO;

public class TestClass {
    public static void Main() {
        string from = @"C:\Users\aceik\AppData\Roaming\GeneratedKits\Badges\default_badge.png";
        string to = @"C:\Users\aceik\AppData\Roaming/GeneratedKits\Badges\default_badge.png";
        Console.WriteLine("From: " + Path.GetFullPath(from));
        Console.WriteLine("To: " + Path.GetFullPath(to));
        Console.WriteLine("Equal: " + Path.GetFullPath(from).Equals(Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase));
    }
}
