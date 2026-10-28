using System.Text;

namespace Velo;

public static partial class Cli
{
    // Commands and their options, for `velo completion`. Keep in step with Help and the Expect(...) calls.
    private static readonly (string Name, string[] Options)[] Commands =
    [
        ("serve", []), ("web", []), ("add", ["-d", "-m", "--model", "--effort"]), ("ls", ["-a", "--all"]), ("show", []),
        ("run", ["-m", "--model", "--effort"]), ("stop", []), ("wait", []), ("logs", ["-f", "--run"]), ("diff", []),
        ("approve", []), ("rm", ["-y"]), ("completion", ["bash", "powershell"]),
    ];
    private static readonly string[] Common = ["--json", "-h", "--help", "-v", "--version"];

    private static string Completion(string shell)
    {
        string commands = string.Join(' ', Commands.Select(c => c.Name)) + ' ' + string.Join(' ', Common);
        var sb = new StringBuilder();
        switch (shell)
        {
            case "bash":
                sb.Append("_velo() {\n  local cur=\"${COMP_WORDS[COMP_CWORD]}\" prev=\"${COMP_WORDS[COMP_CWORD-1]}\" words\n");
                sb.Append($"  if [ \"$COMP_CWORD\" -eq 1 ]; then words=\"{commands}\"\n");
                sb.Append("  elif [ \"$prev\" = \"-d\" ]; then COMPREPLY=($(compgen -d -- \"$cur\")); return\n");
                sb.Append("  else case \"${COMP_WORDS[1]}\" in\n");
                foreach (var (name, options) in Commands.Where(c => c.Options.Length > 0))
                    sb.Append($"    {name}) words=\"{string.Join(' ', options)} --json\" ;;\n");
                sb.Append("    *) words=\"--json\" ;;\n  esac; fi\n");
                sb.Append("  COMPREPLY=($(compgen -W \"$words\" -- \"$cur\"))\n}\ncomplete -F _velo velo");
                return sb.ToString();
            case "powershell":
                sb.Append("Register-ArgumentCompleter -Native -CommandName velo -ScriptBlock {\n  param($word, $ast, $cursor)\n");
                sb.Append("  $t = @($ast.CommandElements | ForEach-Object { $_.ToString() })\n");
                sb.Append("  $options = @{\n");
                foreach (var (name, options) in Commands.Where(c => c.Options.Length > 0))
                    sb.Append($"    '{name}' = '{string.Join(' ', options)} --json'\n");
                sb.Append("  }\n");
                sb.Append($"  $words = if (($word -eq '' -and $t.Count -eq 1) -or ($word -ne '' -and $t.Count -eq 2)) {{ '{commands}' }}\n");
                sb.Append("    elseif ($options.ContainsKey($t[1])) { $options[$t[1]] } else { '--json' }\n");
                sb.Append("  $words -split ' ' | Where-Object { $_ -like \"$word*\" } | ForEach-Object {\n");
                sb.Append("    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }\n}");
                return sb.ToString();
            default:
                throw Usage($"unknown shell '{shell}' (bash or powershell)");
        }
    }
}
