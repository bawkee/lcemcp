using System.Text;

namespace LceMcp;

internal static class ConsoleSecretReader
{
    public static string ReadSecret(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                    builder.Length--;

                continue;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return "";
            }

            if (!char.IsControl(key.KeyChar))
                builder.Append(key.KeyChar);
        }
    }
}
