using System.Diagnostics;
using System.Runtime.InteropServices;

namespace macViz;

internal static class NativeFilePicker
{
    public static string[] PickImageFiles()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return [];
        }

        var scriptLines = new[]
        {
            "set chosenFiles to choose file with prompt \"Select image files\" of type {\"public.image\"} with multiple selections allowed",
            "set outText to \"\"",
            "repeat with f in chosenFiles",
            "set outText to outText & POSIX path of f & linefeed",
            "end repeat",
            "return outText"
        };

        return RunAppleScript(scriptLines);
    }

    public static string? PickFolder()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        var scriptLines = new[]
        {
            "set chosenFolder to choose folder with prompt \"Select image folder\"",
            "return POSIX path of chosenFolder"
        };

        var result = RunAppleScript(scriptLines);
        return result.Length > 0 ? result[0] : null;
    }

    private static string[] RunAppleScript(IReadOnlyList<string> scriptLines)
    {
        try
        {
            var startInfo = new ProcessStartInfo("osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var line in scriptLines)
            {
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(line);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                if (stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
                {
                    return [];
                }

                return [];
            }

            return stdout
                .Split(['\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
