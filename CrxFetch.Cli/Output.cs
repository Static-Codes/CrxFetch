using Spectre.Console;

namespace CrxFetch.Cli;

/// <summary>
/// Console output that keeps the original plain-text wording and the original stdout/stderr
/// split. Values are rendered as <see cref="Text"/> so nothing is ever read as markup, which
/// matters because ids, paths and signed urls can contain square brackets.
/// </summary>
internal static class Output
{
    /// <summary>
    /// Spectre wraps rendered content at the console width, which defaults to 80 columns when
    /// output is redirected. That would fold long signed urls across lines and corrupt the
    /// diagnostics, so the profile is widened past anything we emit.
    /// </summary>
    private const int RenderWidth = 4096;

    /// <summary>
    /// A Spectre console bound to standard error. Spectre only writes to stdout by default, and
    /// the original tool kept diagnostics on stderr, so the split is preserved explicitly.
    /// </summary>
    private static readonly IAnsiConsole ErrorConsole = Create(System.Console.Error);

    /// <summary>
    /// Widens the global console so nothing wraps. Must run before any rendering, including
    /// Spectre's own help output.
    /// </summary>
    public static void ConfigureForUnboundedLines() => AnsiConsole.Console.Profile.Width = RenderWidth;

    /// <summary>Writes one line to standard output.</summary>
    /// <param name="text">The line, written verbatim.</param>
    public static void Line(string text)
    {
        AnsiConsole.Write(new Text(text));
        AnsiConsole.WriteLine();
    }

    /// <summary>Writes one line to standard error.</summary>
    /// <param name="text">The line, written verbatim.</param>
    public static void Error(string text)
    {
        ErrorConsole.Write(new Text(text));
        ErrorConsole.WriteLine();
    }

    /// <summary>Renders a borderless two-column table of named values.</summary>
    /// <param name="fields">The field and value pairs to render, in order.</param>
    public static void Fields(params (string Field, string Value)[] fields)
    {
        var table = new Table()
            .NoBorder()
            .HideHeaders()
            .AddColumn(new TableColumn("field").NoWrap())
            .AddColumn("value");

        foreach (var (field, value) in fields)
        {
            table.AddRow(new Text(field), new Text(value));
        }

        AnsiConsole.Write(table);
    }

    private static IAnsiConsole Create(TextWriter writer) =>
        Configure(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
        }));

    private static IAnsiConsole Configure(IAnsiConsole console)
    {
        console.Profile.Width = RenderWidth;
        return console;
    }
}